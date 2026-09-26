using System.Text.Json;
using Dapper;
using Npgsql;
using Ransys.Domain;
using Ransys.Domain.Attempts;
using Ransys.Domain.Common;
using Ransys.Domain.Fees;
using Ransys.Domain.Monetary;
using Ransys.Domain.Routing;
using Ransys.Domain.Transactions;
using Ransys.Persistence.PostgreSql;
using Ransys.Persistence.PostgreSql.ReferenceData;
using Ransys.Persistence.PostgreSql.Transactions;
using Ransys.Testing.PostgreSql;

namespace Ransys.Persistence.Tests;

[Collection(PostgresCollection.Name)]
public sealed class TransactionStoreTests(PostgresDatabaseFixture db)
{
    private static readonly CurrencyDefinition Idr = CurrencyDefinition.Create("IDR", 1, 2).Value;
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 15, 30, 12, 123, TimeSpan.FromHours(7));

    private readonly TransactionStore _store = new(new ReferenceDataStore());

    [Fact]
    public async Task New_transaction_round_trips_with_canonical_detail_and_history()
    {
        var transaction = NewPayment(withCanonicalDetail: true);

        await using (var session = await PostgresSession.BeginAsync(db.DataSource))
        {
            Assert.True((await _store.InsertAsync(session, transaction)).IsSuccess);
            await session.CommitAsync();
        }

        Assert.Equal(1, transaction.RowVersion);
        Assert.Empty(transaction.PendingStateChanges);

        var loaded = await Load(transaction.Id);
        Assert.Equal(transaction.Identity, loaded.Identity);
        Assert.Equal(Money(100_000m), loaded.Amount);
        Assert.Equal(T0, loaded.ReceivedAt);
        Assert.Equal(ProcessingStatus.Received, loaded.ProcessingStatus);
        Assert.Equal("PLN-123", loaded.Destination!.Identifier);
        Assert.Equal(EndpointType.Biller, loaded.Destination.Type);
        Assert.Equal("0812000111", loaded.Customer!.PhoneNumber);
        Assert.Equal("RRN-1", loaded.References.Rrn);
        Assert.Equal("INVOICE_ID", Assert.Single(loaded.References.ExternalReferences).Type);
        Assert.Equal("R1", loaded.Metadata.Values["product.pln.tariffCode"].GetString());
        Assert.Equal("R1", loaded.Destination.Metadata.Values["product.pln.tariffCode"].GetString());
        Assert.Null(loaded.Fees);
        Assert.Null(loaded.Routing);

        var history = await History(transaction.Id);
        var created = Assert.Single(history);
        Assert.Equal(("PROCESSING", null, "RECEIVED", "CORE"), (created.StatusDimension, created.PreviousStatus, created.NewStatus, created.ChangeSource));
    }

    [Fact]
    public async Task Lifecycle_updates_persist_fees_reserve_routing_and_one_history_row_per_dimension()
    {
        var transaction = NewPayment();
        await Save(transaction);

        await Mutate(transaction.Id, t =>
        {
            t.Validate(Fee2500(), new TransactionConfigurationSnapshot(db.Seed.ConfigVersionId, db.Seed.ConfigVersionId, null, 4), Ctx("VALIDATION_OK"));
            t.MarkReserved(Ctx("PAYMENT_PROCESSING"));
            t.BeginProcessing(RoutingDecision.Initial(ProviderA, 1, T0).Value, Ctx("PROVIDER_PROCESSING"));
        });

        await Mutate(transaction.Id, t =>
            t.CompleteSuccess(TransitionContext.Create("PROVIDER_SUCCESS", ChangeSource.SyncProviderResponse, T0.AddSeconds(2), responseCode: "0000").Value));

        var loaded = await Load(transaction.Id);
        Assert.Equal((ProcessingStatus.Success, FinancialStatus.Posted), (loaded.ProcessingStatus, loaded.FinancialStatus));
        Assert.Equal(Money(102_500m), loaded.ReserveAmount);
        Assert.Equal(Money(2_500m), loaded.Fees!.MerchantChargeTotal);
        Assert.Equal(FeeComponentType.MerchantServiceFee, Assert.Single(loaded.Fees.Items).ComponentType);
        Assert.Equal(ProviderA, loaded.Routing!.InitialProvider);
        Assert.Equal(4, loaded.Configuration.ProviderPolicyVersion);
        Assert.Equal("0000", loaded.ResponseCode);
        Assert.Equal(T0.AddSeconds(2), loaded.FinancialPostedAt);
        Assert.Equal(3, loaded.RowVersion);

        // Rows written in the same millisecond have no guaranteed order (UUIDv7 is random within a millisecond),
        // so the audit trail is compared as a set.
        var history = await History(transaction.Id);
        (string, string?, string)[] expected =
        [
            ("PROCESSING", null, "RECEIVED"),
            ("PROCESSING", "RECEIVED", "VALIDATED"),
            ("FINANCIAL", "NONE", "RESERVED"),
            ("PROCESSING", "VALIDATED", "PROCESSING"),
            ("PROCESSING", "PROCESSING", "SUCCESS"),
            ("FINANCIAL", "RESERVED", "POSTED"),
        ];
        Assert.Equal(
            expected.Order().ToList(),
            history.Select(h => (h.StatusDimension, h.PreviousStatus, h.NewStatus)).Order().ToList());
    }

    [Fact]
    public async Task Failover_routing_audit_round_trips()
    {
        var transaction = NewPayment();
        await Save(transaction);

        await Mutate(transaction.Id, t =>
        {
            t.Validate(Fee2500(), TransactionConfigurationSnapshot.None, Ctx("VALIDATION_OK"));
            t.MarkReserved(Ctx("PAYMENT_PROCESSING"));
            t.BeginProcessing(RoutingDecision.Initial(ProviderA, 7, T0).Value, Ctx("PROVIDER_PROCESSING"));
            var attempt = TransactionAttempt.Start(new AttemptId(Guid.CreateVersion7()), t.Id, 1, AttemptType.Payment, ProviderA, "c", "t", T0).Value;
            attempt.RecordOutcome(AttemptOutcome.Create(false, TransportStatus.ConnectionError).Value);
            Assert.True(t.RecordFailover(attempt, ProviderB, "PROVIDER_LINK_DOWN", T0.AddSeconds(1)).IsSuccess);
        });

        var routing = (await Load(transaction.Id)).Routing!;
        Assert.Equal(ProviderA, routing.InitialProvider);
        Assert.Equal(ProviderB, routing.CurrentProvider);
        Assert.Equal(7, routing.RuleVersion);
        Assert.Equal(1, routing.FailoverCount);
        Assert.Equal("PROVIDER_LINK_DOWN", routing.FailoverReason);
        Assert.Equal(T0.AddSeconds(1), routing.DecisionTimestamp);
    }

    [Fact]
    public async Task Stale_update_is_rejected_as_concurrency_conflict()
    {
        var transaction = NewPayment();
        await Save(transaction);

        await using var sessionA = await PostgresSession.BeginAsync(db.DataSource);
        var copyA = (await _store.GetAsync(sessionA, transaction.Id)).Value!;
        await using (var sessionB = await PostgresSession.BeginAsync(db.DataSource))
        {
            var copyB = (await _store.GetAsync(sessionB, transaction.Id)).Value!;
            copyB.CompleteFailure(Ctx("INVALID_REQUEST"));
            Assert.True((await _store.UpdateAsync(sessionB, copyB)).IsSuccess);
            await sessionB.CommitAsync();
        }

        copyA.Validate(null, TransactionConfigurationSnapshot.None, Ctx("VALIDATION_OK"));
        var result = await _store.UpdateAsync(sessionA, copyA);

        Assert.Equal(ErrorCodes.ConcurrencyConflict, result.Error.Code);
        await sessionA.RollbackAsync();
        Assert.Equal(ProcessingStatus.Failed, (await Load(transaction.Id)).ProcessingStatus);
    }

    [Fact]
    public async Task For_update_lock_blocks_a_second_locker()
    {
        var transaction = NewPayment();
        await Save(transaction);

        await using var holder = await PostgresSession.BeginAsync(db.DataSource);
        Assert.NotNull((await _store.GetAsync(holder, transaction.Id, RowLock.ForUpdate)).Value);

        await using var contender = await PostgresSession.BeginAsync(db.DataSource);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => _store.GetAsync(contender, transaction.Id, RowLock.ForUpdateNoWait));

        Assert.Equal("55P03", ex.SqlState); // lock_not_available
    }

    [Fact]
    public async Task Rolled_back_insert_leaves_nothing()
    {
        var transaction = NewPayment();

        await using (var session = await PostgresSession.BeginAsync(db.DataSource))
        {
            await _store.InsertAsync(session, transaction);
            // disposed without commit
        }

        await using var check = await PostgresSession.BeginAsync(db.DataSource);
        Assert.Null((await _store.GetAsync(check, transaction.Id)).Value);
    }

    [Fact]
    public async Task Unknown_currency_definition_is_a_controlled_failure()
    {
        var usd = CurrencyDefinition.Create("USD", 1, 2).Value;
        var transaction = NewPayment(amount: Domain.Monetary.Money.Create(10m, usd).Value);

        await using var session = await PostgresSession.BeginAsync(db.DataSource);
        var result = await _store.InsertAsync(session, transaction);

        Assert.Equal(ErrorCodes.ReferenceDataNotFound, result.Error.Code);
        Assert.False(transaction.IsPersisted);
    }

    [Fact]
    public async Task Corrupted_persisted_state_fails_closed_on_load()
    {
        var transaction = NewPayment();
        await Save(transaction);

        await using (var connection = await db.DataSource.OpenConnectionAsync())
        {
            // SUCCESS with financial NONE violates the aggregate invariants for a reserving transaction.
            await connection.ExecuteAsync(
                "UPDATE core.transactions SET processing_status = 'SUCCESS' WHERE ransys_transaction_id = @id",
                new { id = transaction.Id.Value });
        }

        await using var session = await PostgresSession.BeginAsync(db.DataSource);
        var result = await _store.GetAsync(session, transaction.Id);

        Assert.Equal(ErrorCodes.PersistedStateInvalid, result.Error.Code);
    }

    [Fact]
    public async Task Missing_transaction_returns_null()
    {
        await using var session = await PostgresSession.BeginAsync(db.DataSource);

        Assert.Null((await _store.GetAsync(session, new TransactionId(Guid.CreateVersion7()))).Value);
    }

    private ProviderReference ProviderA => ProviderReference.Create(new ProviderId(db.Seed.ProviderAId), TestSeed.ProviderACode, TestSeed.ProviderAAdapter).Value;

    private ProviderReference ProviderB => ProviderReference.Create(new ProviderId(db.Seed.ProviderBId), TestSeed.ProviderBCode, TestSeed.ProviderBAdapter).Value;

    private static Money Money(decimal amount) => Domain.Monetary.Money.Create(amount, Idr).Value;

    private static TransitionContext Ctx(string reason) => TransitionContext.Create(reason, ChangeSource.Core, T0).Value;

    private static FeeComponents Fee2500() => FeeComponents.Create(
        [FeeComponent.Create(FeeComponentType.MerchantServiceFee, Money(2_500m), Money(2_500m), FeeBeneficiary.Create("RANSYS").Value, false, 3).Value],
        Idr).Value;

    private Transaction NewPayment(bool withCanonicalDetail = false, Money? amount = null)
    {
        var id = new TransactionId(Guid.CreateVersion7());
        var clientReference = $"INV-{id.Value:N}";
        var merchant = new MerchantId(db.Seed.MerchantId);
        var channel = new ChannelId(db.Seed.ChannelId);
        var product = new ProductId(db.Seed.ProductId);
        var principal = amount ?? Money(100_000m);

        var metadata = ExtensionMetadata.Create([
            new KeyValuePair<string, JsonElement>("product.pln.tariffCode", JsonSerializer.SerializeToElement("R1")),
        ]).Value;
        var destination = withCanonicalDetail ? TransactionEndpoint.Create(EndpointType.Biller, "PLN-123", "PLN", null, metadata).Value : null;
        var customer = withCanonicalDetail ? Customer.Create(phoneNumber: "0812000111", name: "Budi").Value : null;
        var references = withCanonicalDetail
            ? TransactionReferences.Create(clientReference, "M-REF", "000123", "RRN-1",
                externalReferences: [ExternalReference.Create("INVOICE_ID", "INV-77", "MERCHANT", true).Value]).Value
            : TransactionReferences.Create(clientReference).Value;

        var fingerprint = TransactionFingerprint.Compute(new FingerprintInput(
            merchant, channel, TransactionType.Payment, product, null, destination, principal, clientReference));
        var identity = TransactionIdentity.Create(id, clientReference, $"idem-{id.Value:N}", fingerprint).Value;

        return Transaction.Create(new TransactionDraft(
            identity, TransactionType.Payment, merchant, channel, product, principal, customer, null, destination,
            references, withCanonicalDetail ? metadata : ExtensionMetadata.Empty, T0)).Value;
    }

    private async Task Save(Transaction transaction)
    {
        await using var session = await PostgresSession.BeginAsync(db.DataSource);
        Assert.True((await _store.InsertAsync(session, transaction)).IsSuccess);
        await session.CommitAsync();
    }

    private async Task Mutate(TransactionId id, Action<Transaction> change)
    {
        await using var session = await PostgresSession.BeginAsync(db.DataSource);
        var transaction = (await _store.GetAsync(session, id, RowLock.ForUpdate)).Value!;
        change(transaction);
        var result = await _store.UpdateAsync(session, transaction);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : null);
        await session.CommitAsync();
    }

    private async Task<Transaction> Load(TransactionId id)
    {
        await using var session = await PostgresSession.BeginAsync(db.DataSource);
        var result = await _store.GetAsync(session, id);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : null);
        return result.Value!;
    }

    private async Task<List<HistoryRow>> History(TransactionId id)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        var rows = await connection.QueryAsync<HistoryRow>(
            """
            SELECT status_dimension AS StatusDimension, previous_status AS PreviousStatus, new_status AS NewStatus,
                   change_source AS ChangeSource
            FROM core.transaction_state_history
            WHERE ransys_transaction_id = @id
            ORDER BY created_at, history_id
            """,
            new { id = id.Value });
        return rows.ToList();
    }

    private sealed record HistoryRow(string StatusDimension, string? PreviousStatus, string NewStatus, string ChangeSource);
}
