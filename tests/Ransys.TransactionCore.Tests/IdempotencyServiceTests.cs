using Ransys.Application;
using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Monetary;
using Ransys.Domain.Transactions;
using Ransys.TransactionCore.Idempotency;

namespace Ransys.TransactionCore.Tests;

/// <summary>
/// Decision and savepoint flow of <see cref="IdempotencyService"/> with an in-memory store. The unique-index race itself
/// is proven on PostgreSQL in the integration tests.
/// </summary>
public sealed class IdempotencyServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
    private static readonly ChannelId Channel = new(Guid.CreateVersion7());
    private static readonly MerchantId Merchant = new(Guid.CreateVersion7());

    [Fact]
    public async Task Free_reference_creates_the_transaction_under_a_savepoint()
    {
        var (service, store, session) = Setup();
        var identity = Identity(100m);
        var created = 0;

        var decision = await service.ClaimAsync(session, Channel, identity, _ => { created++; return Task.FromResult(Result.Success()); });

        Assert.Equal((IdempotencyOutcome.New, identity.RansysTransactionId), (decision.Value.Outcome, decision.Value.TransactionId));
        Assert.Equal(1, created);
        Assert.Equal(["SAVE", "RELEASE"], session.Savepoints);
        var record = Assert.Single(store.Records);
        Assert.Equal(Now.AddHours(24), record.ExpiresAt);
    }

    [Fact]
    public async Task Same_fingerprint_returns_existing_without_creating()
    {
        var (service, store, session) = Setup();
        var original = Identity(100m);
        store.Records.Add(Record(original, Now.AddHours(1)));
        var retry = Identity(100m, original.ClientReference);

        var decision = await service.ClaimAsync(session, Channel, retry, _ => throw new InvalidOperationException("must not create"));

        Assert.Equal((IdempotencyOutcome.ExistingTransaction, original.RansysTransactionId), (decision.Value.Outcome, decision.Value.TransactionId));
        Assert.Empty(session.Savepoints);
    }

    [Fact]
    public async Task Different_fingerprint_is_a_conflict()
    {
        var (service, store, session) = Setup();
        var original = Identity(100m);
        store.Records.Add(Record(original, Now.AddHours(1)));

        var decision = await service.ClaimAsync(session, Channel, Identity(999m, original.ClientReference), _ => throw new InvalidOperationException());

        Assert.Equal(ErrorCodes.DuplicateReferenceConflict, decision.Error.Code);
    }

    [Fact]
    public async Task Expired_claim_is_expired_lazily_and_the_reference_is_reused()
    {
        var (service, store, session) = Setup();
        var original = Identity(100m);
        store.Records.Add(Record(original, Now.AddSeconds(-1)));

        var decision = await service.ClaimAsync(session, Channel, Identity(500m, original.ClientReference), _ => Task.FromResult(Result.Success()));

        Assert.Equal(IdempotencyOutcome.New, decision.Value.Outcome);
        Assert.False(store.Records[0].Active);
    }

    [Fact]
    public async Task Lost_race_rolls_back_to_the_savepoint_and_returns_the_winner()
    {
        var (service, store, session) = Setup();
        var mine = Identity(100m);
        var winner = Identity(100m, mine.ClientReference);
        store.BeforeInsert = () => store.Records.Add(Record(winner, Now.AddHours(1))); // concurrent request commits first

        var decision = await service.ClaimAsync(session, Channel, mine, _ => Task.FromResult(Result.Success()));

        Assert.Equal((IdempotencyOutcome.ExistingTransaction, winner.RansysTransactionId), (decision.Value.Outcome, decision.Value.TransactionId));
        Assert.Equal(["SAVE", "ROLLBACK_TO"], session.Savepoints);
    }

    [Fact]
    public async Task Failed_creation_rolls_back_to_the_savepoint_and_claims_nothing()
    {
        var (service, store, session) = Setup();

        var decision = await service.ClaimAsync(session, Channel, Identity(100m),
            _ => Task.FromResult(Result.Failure(RansysError.Validation("CREATE_FAILED", "nope"))));

        Assert.Equal("CREATE_FAILED", decision.Error.Code);
        Assert.Empty(store.Records);
        Assert.Equal(["SAVE", "ROLLBACK_TO"], session.Savepoints);
    }

    private static (IdempotencyService Service, FakeStore Store, FakeSession Session) Setup()
    {
        var store = new FakeStore();
        return (new IdempotencyService(store, new FixedClock(), new Ids()), store, new FakeSession());
    }

    private static TransactionIdentity Identity(decimal amount, string? reference = null)
    {
        var clientReference = reference ?? $"INV-{Guid.NewGuid():N}";
        var money = Money.Create(amount, CurrencyDefinition.Create("IDR", 1, 2).Value).Value;
        var fingerprint = TransactionFingerprint.Compute(new FingerprintInput(
            Merchant, Channel, TransactionType.Payment, new ProductId(Guid.Parse("0192a000-0000-7000-8000-000000000003")),
            null, null, money, clientReference));
        return TransactionIdentity.Create(new TransactionId(Guid.CreateVersion7()), clientReference, null, fingerprint).Value;
    }

    private static IdempotencyRecord Record(TransactionIdentity identity, DateTimeOffset expiresAt) =>
        new(Guid.CreateVersion7(), Channel, identity.ClientReference, null, identity.Fingerprint, identity.RansysTransactionId, true, expiresAt.AddHours(-24), expiresAt);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class Ids : IIdGenerator
    {
        public Guid NewId() => Guid.CreateVersion7();
    }

    private sealed class FakeSession : IDatabaseSession
    {
        public List<string> Savepoints { get; } = [];

        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CreateSavepointAsync(string name, CancellationToken cancellationToken = default)
        {
            Savepoints.Add("SAVE");
            return Task.CompletedTask;
        }

        public Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken = default)
        {
            Savepoints.Add("ROLLBACK_TO");
            return Task.CompletedTask;
        }

        public Task ReleaseSavepointAsync(string name, CancellationToken cancellationToken = default)
        {
            Savepoints.Add("RELEASE");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Emulates the partial unique index: at most one active record per channel + reference.</summary>
    private sealed class FakeStore : IIdempotencyStore
    {
        public List<IdempotencyRecord> Records { get; } = [];

        public Action? BeforeInsert { get; set; }

        public Task<Result<IdempotencyRecord?>> FindActiveAsync(
            IDatabaseSession session, ChannelId channelId, string clientReference, CancellationToken cancellationToken) =>
            Task.FromResult(Result<IdempotencyRecord?>.Success(
                Records.SingleOrDefault(r => r.Active && r.ChannelId == channelId && r.ClientReference == clientReference)));

        public Task<bool> ExpireAsync(IDatabaseSession session, Guid recordId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var index = Records.FindIndex(r => r.Id == recordId && r.Active && r.ExpiresAt <= now);
            if (index >= 0)
            {
                Records[index] = Records[index] with { Active = false };
            }

            return Task.FromResult(index >= 0);
        }

        public Task<bool> TryInsertAsync(IDatabaseSession session, IdempotencyRecord record, CancellationToken cancellationToken)
        {
            BeforeInsert?.Invoke();
            BeforeInsert = null;
            if (Records.Any(r => r.Active && r.ChannelId == record.ChannelId && r.ClientReference == record.ClientReference))
            {
                return Task.FromResult(false);
            }

            Records.Add(record);
            return Task.FromResult(true);
        }

        public Task<int> ExpireDueAsync(IDatabaseSession session, DateTimeOffset now, int batchSize, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }
}
