using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Monetary;
using Ransys.Domain.Transactions;
using Ransys.Infrastructure;
using Ransys.Persistence.PostgreSql;
using Ransys.Persistence.PostgreSql.Idempotency;
using Ransys.Persistence.PostgreSql.ReferenceData;
using Ransys.Persistence.PostgreSql.Transactions;
using Ransys.Testing.PostgreSql;
using Ransys.TransactionCore.Idempotency;
using Ransys.Workers;

namespace Ransys.IntegrationTests.Idempotency;

/// <summary>
/// Idempotency on a real PostgreSQL server (PRD §11, SD-05, ADR-009): partial unique index semantics,
/// lazy expiry, and concurrent claims of the same client reference.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class IdempotencyServiceTests(PostgresDatabaseFixture db)
{
    private static readonly CurrencyDefinition Idr = CurrencyDefinition.Create("IDR", 1, 2).Value;

    private readonly IdempotencyService _service = new(new PostgresIdempotencyStore(), new SystemClock(), new UuidV7IdGenerator());
    private readonly TransactionStore _transactions = new(new ReferenceDataStore());

    [Fact]
    public async Task New_reference_is_claimed_with_a_24_hour_window()
    {
        var reference = NewReference();

        var decision = await Claim(reference, 100_000m);

        Assert.Equal(IdempotencyOutcome.New, decision.Value.Outcome);
        var record = await ActiveRecord(reference);
        Assert.Equal(decision.Value.TransactionId.Value, record.TransactionId);
        Assert.Equal(TimeSpan.FromHours(24), record.ExpiresAt - record.CreatedAt);
        Assert.StartsWith("v1:", record.Fingerprint, StringComparison.Ordinal);
        Assert.Equal($"idem-{reference}", record.IdempotencyKey);
    }

    [Fact]
    public async Task Same_reference_and_same_fingerprint_returns_the_existing_transaction()
    {
        var reference = NewReference();
        var first = await Claim(reference, 100_000m);

        var retry = await Claim(reference, 100_000m);

        Assert.Equal(IdempotencyOutcome.ExistingTransaction, retry.Value.Outcome);
        Assert.Equal(first.Value.TransactionId, retry.Value.TransactionId);
        Assert.Equal(1, await TransactionCount(reference));
    }

    [Fact]
    public async Task Same_reference_with_different_fingerprint_is_a_conflict_and_creates_nothing()
    {
        var reference = NewReference();
        await Claim(reference, 100_000m);

        var conflict = await Claim(reference, 150_000m);

        Assert.Equal(ErrorCodes.DuplicateReferenceConflict, conflict.Error.Code);
        Assert.Equal(ErrorCategory.Conflict, conflict.Error.Category);
        Assert.Equal(1, await TransactionCount(reference));
    }

    [Fact]
    public async Task Scope_is_channel_plus_reference()
    {
        var reference = NewReference();
        var otherChannel = await NewChannel();

        var first = await Claim(reference, 100_000m);
        var onOtherChannel = await Claim(reference, 100_000m, otherChannel);

        Assert.Equal(IdempotencyOutcome.New, onOtherChannel.Value.Outcome);
        Assert.NotEqual(first.Value.TransactionId, onOtherChannel.Value.TransactionId);
    }

    [Fact]
    public async Task Expired_claim_is_expired_lazily_and_the_reference_can_be_reused()
    {
        var reference = NewReference();
        var first = await Claim(reference, 100_000m);
        await ForceExpiry(reference);

        var reused = await Claim(reference, 150_000m);

        Assert.Equal(IdempotencyOutcome.New, reused.Value.Outcome);
        Assert.NotEqual(first.Value.TransactionId, reused.Value.TransactionId);
        await using var connection = await db.DataSource.OpenConnectionAsync();
        var old = await connection.QuerySingleAsync<(bool Active, DateTime? ExpiredAt)>(
            "SELECT active, expired_at FROM core.idempotency_records WHERE ransys_transaction_id = @id",
            new { id = first.Value.TransactionId.Value });
        Assert.False(old.Active);
        Assert.NotNull(old.ExpiredAt);
    }

    [Fact]
    public async Task Concurrent_identical_requests_create_exactly_one_transaction()
    {
        var reference = NewReference();

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => Claim(reference, 100_000m))));

        Assert.All(results, r => Assert.True(r.IsSuccess, r.IsFailure ? r.Error.ToString() : null));
        var created = Assert.Single(results, r => r.Value.Outcome == IdempotencyOutcome.New);
        Assert.All(results, r => Assert.Equal(created.Value.TransactionId, r.Value.TransactionId));
        Assert.Equal(1, await TransactionCount(reference));
    }

    [Fact]
    public async Task Concurrent_requests_with_different_payloads_create_one_and_reject_the_rest()
    {
        var reference = NewReference();

        var results = await Task.WhenAll(Enumerable.Range(1, 5).Select(i => Task.Run(() => Claim(reference, i * 1_000m))));

        Assert.Single(results, r => r.IsSuccess && r.Value.Outcome == IdempotencyOutcome.New);
        Assert.Equal(4, results.Count(r => r.IsFailure && r.Error.Code == ErrorCodes.DuplicateReferenceConflict));
        Assert.Equal(1, await TransactionCount(reference));
    }

    [Fact]
    public async Task Failed_transaction_creation_leaves_no_claim_and_the_session_usable()
    {
        var reference = NewReference();
        var transaction = NewPayment(reference, 100_000m, db.Seed.ChannelId);

        await using var session = await PostgresSession.BeginAsync(db.DataSource);
        var result = await _service.ClaimAsync(
            session, transaction.ChannelId, transaction.Identity,
            async ct =>
            {
                await _transactions.InsertAsync(session, transaction, ct);
                return RansysError.Validation("TEST_FAILURE", "simulated failure after insert");
            });

        Assert.Equal("TEST_FAILURE", result.Error.Code);
        Assert.Equal(0, await session.Connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM core.transactions WHERE client_reference = @reference", new { reference }, session.Transaction));
        await session.CommitAsync();
        Assert.Equal(0, await TransactionCount(reference));
    }

    [Fact]
    public async Task Expiry_sweep_expires_only_due_records_in_bounded_batches()
    {
        var due = new[] { NewReference(), NewReference(), NewReference() };
        var notDue = NewReference();
        foreach (var reference in due.Append(notDue))
        {
            await Claim(reference, 100_000m);
        }

        foreach (var reference in due)
        {
            await ForceExpiry(reference);
        }

        var worker = new IdempotencyExpiryWorker(
            db.DataSource, _service, Options.Create(new IdempotencyExpiryOptions { BatchSize = 2 }), NullLogger<IdempotencyExpiryWorker>.Instance);

        var total = 0;
        int expired;
        while ((expired = await worker.RunOnceAsync(CancellationToken.None)) > 0)
        {
            Assert.True(expired <= 2);
            total += expired;
        }

        Assert.True(total >= due.Length); // other tests may leave due records too
        foreach (var reference in due)
        {
            Assert.Equal(0, await ActiveCount(reference));
        }

        Assert.Equal(1, await ActiveCount(notDue));
    }

    private static string NewReference() => $"INV-{Guid.NewGuid():N}";

    private async Task<Result<IdempotencyDecision>> Claim(string reference, decimal amount, Guid? channel = null)
    {
        var transaction = NewPayment(reference, amount, channel ?? db.Seed.ChannelId);

        await using var session = await PostgresSession.BeginAsync(db.DataSource);
        var decision = await _service.ClaimAsync(
            session, transaction.ChannelId, transaction.Identity, ct => _transactions.InsertAsync(session, transaction, ct));
        if (decision.IsSuccess)
        {
            await session.CommitAsync();
        }

        return decision;
    }

    private Transaction NewPayment(string reference, decimal amount, Guid channelId)
    {
        var id = new TransactionId(Guid.CreateVersion7());
        var merchant = new MerchantId(db.Seed.MerchantId);
        var channel = new ChannelId(channelId);
        var product = new ProductId(db.Seed.ProductId);
        var money = Money.Create(amount, Idr).Value;
        var fingerprint = TransactionFingerprint.Compute(new FingerprintInput(
            merchant, channel, TransactionType.Payment, product, null, null, money, reference));

        return Transaction.Create(new TransactionDraft(
            TransactionIdentity.Create(id, reference, $"idem-{reference}", fingerprint).Value,
            TransactionType.Payment, merchant, channel, product, money, null, null, null,
            TransactionReferences.Create(reference).Value, ExtensionMetadata.Empty, DateTimeOffset.UtcNow)).Value;
    }

    private async Task<Guid> NewChannel()
    {
        var id = Guid.CreateVersion7();
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            "INSERT INTO core.channels VALUES (@id, @merchant, @code, 'REST', 'SIGNED_API', 'ACTIVE', now(), now())",
            new { id, merchant = db.Seed.MerchantId, code = $"CH-{id:N}" });
        return id;
    }

    private async Task ForceExpiry(string reference)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            UPDATE core.idempotency_records
            SET created_at = now() - interval '25 hours', expires_at = now() - interval '1 hour'
            WHERE client_reference = @reference AND active
            """,
            new { reference });
    }

    private async Task<int> TransactionCount(string reference)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM core.transactions WHERE client_reference = @reference", new { reference });
    }

    private async Task<int> ActiveCount(string reference)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM core.idempotency_records WHERE client_reference = @reference AND active", new { reference });
    }

    private async Task<RecordRow> ActiveRecord(string reference)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<RecordRow>(
            """
            SELECT ransys_transaction_id AS TransactionId, fingerprint AS Fingerprint, idempotency_key AS IdempotencyKey,
                   created_at AS CreatedAt, expires_at AS ExpiresAt
            FROM core.idempotency_records WHERE client_reference = @reference AND active
            """,
            new { reference });
    }

    private sealed record RecordRow(Guid TransactionId, string Fingerprint, string? IdempotencyKey, DateTime CreatedAt, DateTime ExpiresAt);
}
