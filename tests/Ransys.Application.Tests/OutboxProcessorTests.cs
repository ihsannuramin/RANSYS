using Ransys.Application;
using Ransys.Application.Outbox;

namespace Ransys.Application.Tests;

/// <summary>Decision logic of the outbox processor, isolated from PostgreSQL (claiming semantics are tested on a real database).</summary>
public sealed class OutboxProcessorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);

    private static readonly OutboxOptions Options = new()
    {
        MaxAttempts = 3,
        BaseRetryDelay = TimeSpan.FromSeconds(5),
        MaxRetryDelay = TimeSpan.FromSeconds(30),
        PublishTimeout = TimeSpan.FromMilliseconds(200),
    };

    [Fact]
    public async Task Successful_publish_marks_the_event_published()
    {
        var store = new FakeStore(Envelope(retryCount: 0));

        var result = await Processor(store, _ => Task.FromResult(PublishResult.Success)).ProcessBatchAsync();

        Assert.Equal(new OutboxBatchResult(1, 1, 0, 0, 0), result);
        Assert.True(store.Completions.Single().Succeeded);
    }

    [Fact]
    public async Task Failure_schedules_a_retry_with_exponential_backoff()
    {
        var store = new FakeStore(Envelope(retryCount: 1));

        var result = await Processor(store, _ => Task.FromResult(PublishResult.Failure("DOWN", "consumer down"))).ProcessBatchAsync();

        Assert.Equal(1, result.Retrying);
        var failure = store.Failures.Single();
        Assert.Equal((2, false, Now.AddSeconds(10)), (failure.RetryCount, failure.Dead, failure.NextRetryAt));
        Assert.Equal("DOWN", store.Completions.Single().ErrorCode);
    }

    [Fact]
    public async Task Last_allowed_failure_makes_the_event_dead()
    {
        var store = new FakeStore(Envelope(retryCount: 2));

        var result = await Processor(store, _ => Task.FromResult(PublishResult.Failure("DOWN", "consumer down"))).ProcessBatchAsync();

        Assert.Equal(1, result.Dead);
        Assert.Equal((3, true, (DateTimeOffset?)null), (store.Failures.Single().RetryCount, store.Failures.Single().Dead, store.Failures.Single().NextRetryAt));
    }

    [Fact]
    public async Task Exceptions_and_timeouts_are_failed_deliveries()
    {
        var throwing = new FakeStore(Envelope(0));
        await Processor(throwing, _ => throw new InvalidOperationException("boom")).ProcessBatchAsync();

        var slow = new FakeStore(Envelope(0));
        await Processor(slow, async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return PublishResult.Success;
        }).ProcessBatchAsync();

        Assert.Equal("PUBLISH_EXCEPTION", throwing.Completions.Single().ErrorCode);
        Assert.Equal("PUBLISH_TIMEOUT", slow.Completions.Single().ErrorCode);
    }

    [Fact]
    public async Task Lost_lease_is_counted_and_not_treated_as_published()
    {
        var store = new FakeStore(Envelope(0)) { OwnsLease = false };

        var result = await Processor(store, _ => Task.FromResult(PublishResult.Success)).ProcessBatchAsync();

        Assert.Equal(new OutboxBatchResult(1, 0, 0, 0, 1), result);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 30)]
    [InlineData(40, 30)]
    public void Retry_delay_doubles_and_is_capped(int retry, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), Processor(new FakeStore(), _ => Task.FromResult(PublishResult.Success)).RetryDelay(retry));
    }

    private static OutboxEnvelope Envelope(int retryCount) =>
        new(Guid.CreateVersion7(), "TEST", Guid.CreateVersion7(), "TEST_EVENT", 1, 1, "{}", retryCount, Now);

    private static OutboxProcessor Processor(FakeStore store, Func<CancellationToken, Task<PublishResult>> publish) =>
        new(new FakeSessions(), store, new FakePublisher(publish), new FixedClock(), Options, "worker-1");

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class FakePublisher(Func<CancellationToken, Task<PublishResult>> publish) : IOutboxPublisher
    {
        public string Transport => "FAKE";

        public Task<PublishResult> PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken) => publish(cancellationToken);
    }

    private sealed class FakeSessions : IDatabaseSessionFactory
    {
        public Task<IDatabaseSession> BeginAsync(CancellationToken cancellationToken = default) => Task.FromResult<IDatabaseSession>(new FakeSession());
    }

    private sealed class FakeSession : IDatabaseSession
    {
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CreateSavepointAsync(string name, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ReleaseSavepointAsync(string name, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeStore(params OutboxEnvelope[] claimable) : IOutboxStore
    {
        public bool OwnsLease { get; init; } = true;

        public List<OutboxDeliveryAttempt> Completions { get; } = [];

        public List<(int RetryCount, bool Dead, DateTimeOffset? NextRetryAt)> Failures { get; } = [];

        public Task<IReadOnlyList<OutboxEnvelope>> ClaimBatchAsync(
            IDatabaseSession session, string workerId, DateTimeOffset now, TimeSpan lease, int batchSize, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OutboxEnvelope>>(claimable);

        public Task<bool> MarkPublishedAsync(
            IDatabaseSession session, Guid eventId, string workerId, OutboxDeliveryAttempt attempt, CancellationToken cancellationToken = default)
        {
            Completions.Add(attempt);
            return Task.FromResult(OwnsLease);
        }

        public Task<bool> MarkFailedAsync(
            IDatabaseSession session, Guid eventId, string workerId, int retryCount, bool dead, DateTimeOffset? nextRetryAt,
            OutboxDeliveryAttempt attempt, CancellationToken cancellationToken = default)
        {
            Completions.Add(attempt);
            Failures.Add((retryCount, dead, nextRetryAt));
            return Task.FromResult(OwnsLease);
        }
    }
}
