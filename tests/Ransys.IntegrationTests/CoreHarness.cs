using Dapper;
using Ransys.Application;
using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Fees;
using Ransys.Domain.Monetary;
using Ransys.Domain.Routing;
using Ransys.Domain.Transactions;
using Ransys.Infrastructure;
using Ransys.Ledger;
using Ransys.Persistence.PostgreSql;
using Ransys.Persistence.PostgreSql.Attempts;
using Ransys.Persistence.PostgreSql.Ledger;
using Ransys.Persistence.PostgreSql.Outbox;
using Ransys.Persistence.PostgreSql.ReferenceData;
using Ransys.Persistence.PostgreSql.Transactions;
using Ransys.Testing.PostgreSql;
using Ransys.TransactionCore.Attempts;
using Ransys.TransactionCore.Finalization;
using Ransys.TransactionCore.Idempotency;
using Ransys.TransactionCore.Providers;
using Ransys.TransactionCore.Reversal;
using Ransys.Persistence.PostgreSql.Idempotency;
using Ransys.Persistence.PostgreSql.Routing;

namespace Ransys.IntegrationTests;

/// <summary>Real Transaction Core services wired to PostgreSQL, with helpers to reach common states.</summary>
internal sealed class CoreHarness
{
    public static readonly CurrencyDefinition Idr = CurrencyDefinition.Create("IDR", 1, 2).Value;

    private readonly PostgresDatabaseFixture _db;

    public CoreHarness(PostgresDatabaseFixture db)
    {
        _db = db;
        var clock = new SystemClock();
        var ids = new UuidV7IdGenerator();
        Transactions = new TransactionStore(new ReferenceDataStore());
        AttemptStore = new PostgresTransactionAttemptStore();
        Ledger = new LedgerPostingService(new PostgresLedgerStore(new ReferenceDataStore()), new PostgresOutboxWriter(), clock, ids);
        Attempts = new TransactionAttemptService(AttemptStore, clock, ids);
        Recovery = new AttemptRecoveryService(Transactions, AttemptStore, Attempts, Ledger, clock);
        Finalization = new TransactionFinalizationService(
            new PostgresSessionFactory(db.DataSource), Transactions, Ledger, new PostgresOutboxWriter(), clock, ids);
        Reversals = new ReversalService(
            Transactions, new IdempotencyService(new PostgresIdempotencyStore(), clock, ids), new PostgresRoutingStore(),
            new PostgresOutboxWriter(), clock, ids);
        CallbackSink = new ProviderCallbackSink(
            new PostgresSessionFactory(db.DataSource), Transactions, AttemptStore, Attempts, Finalization, clock);
    }

    public ProviderCallbackSink CallbackSink { get; }

    public ReversalService Reversals { get; }

    public TransactionFinalizationService Finalization { get; }

    public TransactionStore Transactions { get; }

    public PostgresTransactionAttemptStore AttemptStore { get; }

    public LedgerPostingService Ledger { get; }

    public TransactionAttemptService Attempts { get; }

    public AttemptRecoveryService Recovery { get; }

    public ProviderReference ProviderA => ProviderReference.Create(new ProviderId(_db.Seed.ProviderAId), TestSeed.ProviderACode, TestSeed.ProviderAAdapter).Value;

    public ProviderReference ProviderB => ProviderReference.Create(new ProviderId(_db.Seed.ProviderBId), TestSeed.ProviderBCode, TestSeed.ProviderBAdapter).Value;

    public static Money Rp(decimal amount) => Money.Create(amount, Idr).Value;

    public static TransitionContext Ctx(string reason, ChangeSource source = ChangeSource.Core, Domain.AttemptId? attempt = null) =>
        TransitionContext.Create(reason, source, DateTimeOffset.UtcNow, attemptId: attempt).Value;

    public Task<PostgresSession> Session() => PostgresSession.BeginAsync(_db.DataSource);

    /// <summary>Payment of 100,000 + 2,500 fee, reserved on a funded wallet and PROCESSING on provider A (SD-01 up to the provider call).</summary>
    public async Task<(TransactionId Transaction, WalletId Wallet)> ProcessingPayment(
        WalletId? sharedWallet = null, ProviderReference? provider = null, FeeRefundPolicy feePolicy = FeeRefundPolicy.None)
    {
        var wallet = sharedWallet ?? await NewFundedWallet(1_000_000m);
        var routedTo = provider ?? ProviderA;
        var transaction = NewPayment(100_000m);
        transaction.Validate(Fee2500(feePolicy), TransactionConfigurationSnapshot.None, Ctx("VALIDATION_OK"));

        await using var session = await Session();
        Ok(await Transactions.InsertAsync(session, transaction));
        Ok(transaction.MarkReserved(Ctx("PAYMENT_PROCESSING")));
        Ok(await Ledger.ReserveAsync(session, new ReserveRequest(transaction.Id, wallet, transaction.Amount, transaction.Fees!.GuaranteedReserveFeeTotal, "PAYMENT_PROCESSING")));
        Ok(transaction.BeginProcessing(RoutingDecision.Initial(routedTo, 1, DateTimeOffset.UtcNow).Value, Ctx("PROVIDER_PROCESSING")));
        Ok(await Transactions.UpdateAsync(session, transaction));
        await session.CommitAsync();
        return (transaction.Id, wallet);
    }

    /// <summary>
    /// Starts (and persists) the primary attempt on a PROCESSING transaction, as Session 1 always does before the real
    /// provider call (ADR-005). <see cref="ProcessingPayment"/> stops short of this so tests can drive the attempt
    /// themselves; callback correlation needs one to exist.
    /// </summary>
    public async Task<Domain.Attempts.TransactionAttempt> StartAttempt(
        TransactionId tx, ProviderReference provider, Domain.Attempts.AttemptType attemptType = Domain.Attempts.AttemptType.Payment)
    {
        await using var session = await Session();
        var transaction = Ok(await Transactions.GetAsync(session, tx, Persistence.PostgreSql.Transactions.RowLock.ForUpdate))!;
        var attempt = Ok(await Attempts.StartAsync(session, transaction, attemptType, provider, "corr-1", "trace-1"));
        await session.CommitAsync();
        return attempt;
    }

    /// <summary>
    /// A REFUND / VOID child of <paramref name="original"/> in PROCESSING on the original's provider, inserted directly
    /// (the processing service creates children through idempotency; this only reaches the state for finalization tests).
    /// </summary>
    public async Task<TransactionId> ProcessingChild(TransactionId original, TransactionType type, decimal? amount = null)
    {
        var parent = await Load(original);
        var id = new TransactionId(Guid.CreateVersion7());
        var reference = $"CH-{id.Value:N}";
        var money = amount is { } a ? Rp(a) : parent.Amount;
        var fingerprint = TransactionFingerprint.Compute(new FingerprintInput(
            parent.MerchantId, parent.ChannelId, type, parent.ProductId, null, null, money, reference));
        var child = Transaction.Create(new TransactionDraft(
            TransactionIdentity.Create(id, reference, null, fingerprint, original).Value,
            type, parent.MerchantId, parent.ChannelId, parent.ProductId, money, null, null, null,
            TransactionReferences.Create(reference).Value, ExtensionMetadata.Empty, DateTimeOffset.UtcNow)).Value;
        Ok(child.Validate(null, parent.Configuration, Ctx("VALIDATION_OK")));
        Ok(child.BeginProcessing(RoutingDecision.Initial(parent.Routing!.CurrentProvider, 1, DateTimeOffset.UtcNow).Value, Ctx("PROVIDER_PROCESSING")));

        await using var session = await Session();
        Ok(await Transactions.InsertAsync(session, child));

        // A real child always carries the attempt the provider answered (ADR-005); the callback sink correlates on it.
        Ok(await Attempts.StartAsync(
            session, child, Transaction.PrimaryAttemptTypeFor(type)!.Value, parent.Routing.CurrentProvider, "corr-child", "trace-child"));
        await session.CommitAsync();
        return id;
    }

    /// <summary>Gives a provider a capability (idempotent).</summary>
    public async Task GrantCapability(ProviderId provider, string capability)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            "INSERT INTO integration.provider_capabilities VALUES (@provider, @capability, true, '{}'::jsonb, now()) ON CONFLICT DO NOTHING",
            new { provider = provider.Value, capability });
    }

    public async Task<Transaction> Load(TransactionId id)
    {
        await using var session = await Session();
        return Ok(await Transactions.GetAsync(session, id))!;
    }

    public async Task<IReadOnlyList<Domain.Attempts.TransactionAttempt>> LoadAttempts(TransactionId id)
    {
        await using var session = await Session();
        return Ok(await AttemptStore.GetByTransactionAsync(session, id));
    }

    public async Task<T> Query<T>(string sql, object parameters)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<T>(sql, parameters);
    }

    public async Task<WalletId> NewFundedWallet(decimal balance)
    {
        var merchant = Guid.CreateVersion7();
        var wallet = new WalletId(Guid.CreateVersion7());
        await using (var connection = await _db.DataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO core.merchants VALUES (@merchant, @code, 'Core Test Merchant', 'ACTIVE', now(), now());
                INSERT INTO ledger.wallets
                    (wallet_id, merchant_id, product_id, currency_definition_id, ledger_balance, available_balance,
                     reserved_balance, status, created_at, updated_at)
                VALUES (@wallet, @merchant, NULL, @currency, 0, 0, 0, 'ACTIVE', now(), now());
                """,
                new { merchant, code = $"MRC-{merchant:N}", wallet = wallet.Value, currency = _db.Seed.IdrV1CurrencyDefinitionId });
        }

        await using var session = await Session();
        Ok(await Ledger.PostTopUpAsync(session, new TopUpRequest(wallet, $"TP-{Guid.NewGuid():N}", Rp(balance), null, Domain.Ledger.LedgerActor.System)));
        await session.CommitAsync();
        return wallet;
    }

    public static T Ok<T>(Domain.Common.Result<T> result) =>
        result.IsSuccess ? result.Value : throw new InvalidOperationException(result.Error.ToString());

    public static void Ok(Domain.Common.Result result)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error.ToString());
        }
    }

    private Transaction NewPayment(decimal amount)
    {
        var id = new TransactionId(Guid.CreateVersion7());
        var reference = $"INV-{id.Value:N}";
        var merchant = new MerchantId(_db.Seed.MerchantId);
        var channel = new ChannelId(_db.Seed.ChannelId);
        var product = new ProductId(_db.Seed.ProductId);
        var fingerprint = TransactionFingerprint.Compute(new FingerprintInput(
            merchant, channel, TransactionType.Payment, product, null, null, Rp(amount), reference));

        return Transaction.Create(new TransactionDraft(
            TransactionIdentity.Create(id, reference, null, fingerprint).Value,
            TransactionType.Payment, merchant, channel, product, Rp(amount), null, null, null,
            TransactionReferences.Create(reference).Value, ExtensionMetadata.Empty, DateTimeOffset.UtcNow)).Value;
    }

    private static FeeComponents Fee2500(FeeRefundPolicy policy = FeeRefundPolicy.None) => FeeComponents.Create(
        [FeeComponent.Create(FeeComponentType.MerchantServiceFee, Rp(2_500m), Rp(2_500m), FeeBeneficiary.Create("RANSYS").Value, policy, 1).Value],
        Idr).Value;
}
