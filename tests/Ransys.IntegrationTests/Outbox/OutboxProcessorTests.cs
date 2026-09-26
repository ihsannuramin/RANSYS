using System.Collections.Concurrent;
using Dapper;
using Ransys.Application;
using Ransys.Application.Outbox;
using Ransys.Persistence.PostgreSql;
using Ransys.Persistence.PostgreSql.Outbox;
using Ransys.Testing.PostgreSql;

namespace Ransys.IntegrationTests.Outbox;

/// <summary>
/// Brokerless transactional outbox on a real PostgreSQL server (main.md §18, Architecture Spec §38–40).
/// The outbox table is shared with other tests, so assertions only look at events created by each test.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OutboxProcessorTests(PostgresDatabaseFixture db)
{
    private static readonly OutboxOptions Options = new()
    {
        BatchSize = 10,
        LeaseDuration = TimeSpan.FromMinutes(5),
        MaxAttempts = 3,
        BaseRetryDelay = TimeSpan.FromSeconds(5),
        MaxRetryDelay = TimeSpan.FromMinutes(1),
        PublishTimeout = TimeSpan.FromSeconds(5),
    };

    private readonly FakeClock _clock = new(DateTimeOffset.UtcNow);

    [Fact]
    public async Task Pending_events_are_published_once_with_a_delivery_attempt()
    {
        var events = await Enqueue(3);
        var publisher = new RecordingPublisher();

        await DrainAsync(Processor(publisher), events);

        Assert.Equal(events.Order(), publisher.PublishedIds(events).Order());
        foreach (var id in events)
        {
            var state = await State(id);
            Assert.Equal(("PUBLISHED", 0, (string?)null), (state.Status, state.RetryCount, state.LockedBy));
            Assert.NotNull(state.PublishedAt);
            Assert.Equal([(1, "SUCCESS", "TEST")], await Attempts(id));
        }
    }

    [Fact]
    public async Task Failed_delivery_is_retried_after_backoff_then_published()
    {
        var id = Assert.Single(await Enqueue(1));
        var publisher = new RecordingPublisher { FailuresBeforeSuccess = 1, Only = [id] };
        var processor = Processor(publisher);

        await processor.ProcessBatchAsync();
        var afterFailure = await State(id);
        Assert.Equal(("PENDING", 1), (afterFailure.Status, afterFailure.RetryCount));
        Assert.Equal(_clock.UtcNow.AddSeconds(5).UtcDateTime, afterFailure.NextRetryAt!.Value, TimeSpan.FromMilliseconds(1));

        await processor.ProcessBatchAsync(); // before next_retry_at: not claimed
        Assert.Equal(1, publisher.Calls(id));

        _clock.Advance(TimeSpan.FromSeconds(6));
        await processor.ProcessBatchAsync();

        Assert.Equal("PUBLISHED", (await State(id)).Status);
        Assert.Equal([(1, "FAILED", "TEST"), (2, "SUCCESS", "TEST")], await Attempts(id));
    }

    [Fact]
    public async Task Event_becomes_dead_after_max_attempts_and_is_never_claimed_again()
    {
        var id = Assert.Single(await Enqueue(1));
        var publisher = new RecordingPublisher { FailuresBeforeSuccess = int.MaxValue, Only = [id] };
        var processor = Processor(publisher);

        for (var i = 0; i < Options.MaxAttempts; i++)
        {
            await processor.ProcessBatchAsync();
            _clock.Advance(TimeSpan.FromMinutes(2));
        }

        var dead = await State(id);
        Assert.Equal(("DEAD", 3), (dead.Status, dead.RetryCount));

        await processor.ProcessBatchAsync();
        Assert.Equal(3, publisher.Calls(id));
        Assert.Equal(3, (await Attempts(id)).Count);
    }

    [Fact]
    public async Task Publisher_exception_is_a_failed_delivery_not_a_lost_event()
    {
        var id = Assert.Single(await Enqueue(1));
        var publisher = new RecordingPublisher { ThrowFor = [id] };

        var result = await Processor(publisher).ProcessBatchAsync();

        Assert.True(result.Retrying >= 1);
        var state = await State(id);
        Assert.Equal(("PENDING", 1), (state.Status, state.RetryCount));
        Assert.Equal("PUBLISH_EXCEPTION", await ErrorCode(id));
    }

    [Fact]
    public async Task Expired_lease_of_a_crashed_worker_is_reclaimed()
    {
        var id = Assert.Single(await Enqueue(1));
        await using (var connection = await db.DataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE async.outbox_events SET status = 'PROCESSING', locked_by = 'crashed-worker', locked_at = @at WHERE event_id = @id",
                new { id, at = _clock.UtcNow.AddMinutes(-10).UtcDateTime });
        }

        var publisher = new RecordingPublisher();
        await Processor(publisher).ProcessBatchAsync();

        Assert.Equal(1, publisher.Calls(id));
        Assert.Equal("PUBLISHED", (await State(id)).Status);
    }

    [Fact]
    public async Task Worker_that_lost_its_lease_cannot_overwrite_the_new_owner()
    {
        var id = Assert.Single(await Enqueue(1));
        var store = new PostgresOutboxStore();

        await using (var session = await PostgresSession.BeginAsync(db.DataSource))
        {
            Assert.Contains(await store.ClaimBatchAsync(session, "slow-worker", _clock.UtcNow, Options.LeaseDuration, 1000, default), e => e.EventId == id);
            await session.CommitAsync();
        }

        _clock.Advance(TimeSpan.FromMinutes(6));
        await Processor(new RecordingPublisher()).ProcessBatchAsync(); // reclaims and publishes

        await using var late = await PostgresSession.BeginAsync(db.DataSource);
        var attempt = new OutboxDeliveryAttempt("TEST", false, "LATE", "late failure", _clock.UtcNow, _clock.UtcNow);
        Assert.False(await store.MarkFailedAsync(late, id, "slow-worker", 1, false, _clock.UtcNow, attempt));
        await late.CommitAsync();

        Assert.Equal("PUBLISHED", (await State(id)).Status);
    }

    [Fact]
    public async Task Two_parallel_workers_never_claim_the_same_event()
    {
        var events = await Enqueue(50);
        var publisher = new RecordingPublisher { Delay = TimeSpan.FromMilliseconds(5) };
        var workerA = Processor(publisher, "worker-A");
        var workerB = Processor(publisher, "worker-B");

        for (var round = 0; round < 50 && await PendingCount(events) > 0; round++)
        {
            await Task.WhenAll(
                Task.Run(() => workerA.ProcessBatchAsync()),
                Task.Run(() => workerB.ProcessBatchAsync()));
        }

        Assert.Equal(0, await PendingCount(events));
        Assert.All(events, id => Assert.Equal(1, publisher.Calls(id)));
        Assert.True(publisher.PublishedBy("worker-A", events) > 0 && publisher.PublishedBy("worker-B", events) > 0,
            "both workers should have taken part");
    }

    [Fact]
    public async Task Ledger_posting_event_is_delivered_with_its_payload()
    {
        var harness = new CoreHarness(db);
        var wallet = await harness.NewFundedWallet(10_000m); // writes TOPUP_POSTED to the outbox
        Guid id;
        await using (var connection = await db.DataSource.OpenConnectionAsync())
        {
            id = await connection.ExecuteScalarAsync<Guid>(
                "SELECT event_id FROM async.outbox_events WHERE aggregate_id = @wallet AND event_type = 'TOPUP_POSTED'",
                new { wallet = wallet.Value });
        }

        var publisher = new RecordingPublisher();

        await DrainAsync(Processor(publisher, clock: new FakeClock(DateTimeOffset.UtcNow.AddSeconds(1))), [id]);

        var envelope = publisher.Envelope(id);
        Assert.Equal(("WALLET", "TOPUP_POSTED", 1), (envelope.AggregateType, envelope.EventType, envelope.EventVersion));
        using var payload = System.Text.Json.JsonDocument.Parse(envelope.PayloadJson);
        Assert.StartsWith("TOPUP:", payload.RootElement.GetProperty("postingKey").GetString(), StringComparison.Ordinal);
        Assert.Equal(10_000m, payload.RootElement.GetProperty("availableBalanceAfter").GetDecimal());
        Assert.Equal(wallet.Value, payload.RootElement.GetProperty("walletId").GetGuid());
    }

    [Fact]
    public void Retry_delay_is_exponential_and_capped()
    {
        var processor = Processor(new RecordingPublisher());

        Assert.Equal(TimeSpan.FromSeconds(5), processor.RetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(10), processor.RetryDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(40), processor.RetryDelay(4));
        Assert.Equal(TimeSpan.FromMinutes(1), processor.RetryDelay(10));
    }

    private OutboxProcessor Processor(RecordingPublisher publisher, string workerId = "test-worker", IClock? clock = null) =>
        new(new PostgresSessionFactory(db.DataSource), new PostgresOutboxStore(), publisher.For(workerId), clock ?? _clock, Options, workerId);

    private async Task DrainAsync(OutboxProcessor processor, IReadOnlyCollection<Guid> events)
    {
        for (var round = 0; round < 100 && await PendingCount(events) > 0; round++)
        {
            await processor.ProcessBatchAsync();
        }
    }

    private async Task<List<Guid>> Enqueue(int count)
    {
        var aggregate = Guid.CreateVersion7();
        var ids = Enumerable.Range(0, count).Select(_ => Guid.CreateVersion7()).ToList();
        var writer = new PostgresOutboxWriter();

        await using var session = await PostgresSession.BeginAsync(db.DataSource);
        var version = 1L;
        foreach (var id in ids)
        {
            // Created slightly in the past so the fake clock can always claim them.
            await writer.EnqueueAsync(session, new OutboxMessage(
                id, "TEST_AGGREGATE", aggregate, "TEST_EVENT", 1, version++, new { sequence = version }, _clock.UtcNow.AddSeconds(-1)));
        }

        await session.CommitAsync();
        return ids;
    }

    private async Task<(string Status, int RetryCount, string? LockedBy, DateTime? PublishedAt, DateTime? NextRetryAt)> State(Guid id)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<(string, int, string?, DateTime?, DateTime?)>(
            "SELECT status, retry_count, locked_by, published_at, next_retry_at FROM async.outbox_events WHERE event_id = @id", new { id });
    }

    private async Task<List<(int AttemptNo, string Result, string Transport)>> Attempts(Guid id)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        var rows = await connection.QueryAsync<(int, string, string)>(
            "SELECT attempt_no, result, transport FROM async.outbox_delivery_attempts WHERE event_id = @id ORDER BY attempt_no", new { id });
        return rows.ToList();
    }

    private async Task<string?> ErrorCode(Guid id)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<string?>(
            "SELECT error_code FROM async.outbox_delivery_attempts WHERE event_id = @id ORDER BY attempt_no DESC LIMIT 1", new { id });
    }

    private async Task<int> PendingCount(IReadOnlyCollection<Guid> events)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM async.outbox_events WHERE event_id = ANY(@ids) AND status IN ('PENDING', 'PROCESSING')",
            new { ids = events.ToArray() });
    }

    private sealed class FakeClock(DateTimeOffset start) : IClock
    {
        private DateTimeOffset _now = start;

        public DateTimeOffset UtcNow => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>Records every publish; can fail or throw for selected events only (other tests' events always succeed).</summary>
    private sealed class RecordingPublisher
    {
        private readonly ConcurrentDictionary<Guid, int> _calls = new();
        private readonly ConcurrentDictionary<Guid, (string Worker, OutboxEnvelope Envelope)> _published = new();

        public int FailuresBeforeSuccess { get; init; }

        public HashSet<Guid> Only { get; init; } = [];

        public HashSet<Guid> ThrowFor { get; init; } = [];

        public TimeSpan Delay { get; init; }

        public IOutboxPublisher For(string workerId) => new View(this, workerId);

        public int Calls(Guid id) => _calls.GetValueOrDefault(id);

        public IEnumerable<Guid> PublishedIds(IEnumerable<Guid> ids) => ids.Where(_published.ContainsKey);

        public int PublishedBy(string worker, IEnumerable<Guid> ids) => ids.Count(id => _published.TryGetValue(id, out var p) && p.Worker == worker);

        public OutboxEnvelope Envelope(Guid id) => _published[id].Envelope;

        private async Task<PublishResult> PublishAsync(string worker, OutboxEnvelope envelope)
        {
            var call = _calls.AddOrUpdate(envelope.EventId, 1, (_, n) => n + 1);
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay);
            }

            if (ThrowFor.Contains(envelope.EventId))
            {
                throw new InvalidOperationException("consumer unavailable");
            }

            if (Only.Contains(envelope.EventId) && call <= FailuresBeforeSuccess)
            {
                return PublishResult.Failure("TEST_FAILURE", "simulated failure");
            }

            _published[envelope.EventId] = (worker, envelope);
            return PublishResult.Success;
        }

        private sealed class View(RecordingPublisher owner, string worker) : IOutboxPublisher
        {
            public string Transport => "TEST";

            public Task<PublishResult> PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken) =>
                owner.PublishAsync(worker, envelope);
        }
    }
}
