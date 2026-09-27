using Dapper;
using Npgsql;
using Ransys.Configuration;
using Ransys.Domain;
using Ransys.Domain.Ledger;
using Ransys.Domain.Routing;
using Ransys.Infrastructure;
using Ransys.Ledger;
using Ransys.Persistence.PostgreSql;
using Ransys.Persistence.PostgreSql.Attempts;
using Ransys.Persistence.PostgreSql.Configuration;
using Ransys.Persistence.PostgreSql.Fees;
using Ransys.Persistence.PostgreSql.Idempotency;
using Ransys.Persistence.PostgreSql.Ledger;
using Ransys.Persistence.PostgreSql.Outbox;
using Ransys.Persistence.PostgreSql.Queries;
using Ransys.Persistence.PostgreSql.ReferenceData;
using Ransys.Persistence.PostgreSql.Routing;
using Ransys.Persistence.PostgreSql.Transactions;
using Ransys.Routing;
using Ransys.Testing;
using Ransys.Testing.PostgreSql;
using Ransys.TransactionCore.Attempts;
using Ransys.TransactionCore.Children;
using Ransys.TransactionCore.Fees;
using Ransys.TransactionCore.Finalization;
using Ransys.TransactionCore.Idempotency;
using Ransys.TransactionCore.Processing;
using Ransys.TransactionCore.Providers;
using Ransys.TransactionCore.Reversal;

namespace Ransys.IntegrationTests.Processing;

/// <summary>One merchant + channel + funded main wallet + product + providers (scripted adapters) for a test.</summary>
internal sealed record Scenario(
    MerchantId Merchant,
    ChannelId Channel,
    WalletId Wallet,
    ProductId Product,
    string ProductCode,
    IReadOnlyList<(ProviderId Id, ScriptedProviderAdapter Adapter)> Providers)
{
    public ScriptedProviderAdapter Adapter => Providers[0].Adapter;
}

/// <summary>
/// The full M12d service graph on real PostgreSQL stores, with an in-process adapter registry. The composition mirrors
/// what the API host will wire.
/// </summary>
internal sealed class ProcessingHarness
{
    public static readonly ProviderCallPolicy TestPolicy = new(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(400));

    private readonly PostgresDatabaseFixture _db;

    public ProcessingHarness(PostgresDatabaseFixture db, NpgsqlDataSource? dataSource = null)
    {
        _db = db;
        Core = new CoreHarness(db);
        var clock = new SystemClock();
        var ids = new UuidV7IdGenerator();
        var sessions = new PostgresSessionFactory(dataSource ?? db.DataSource);
        var transactions = new TransactionStore(new ReferenceDataStore());
        var attemptStore = new PostgresTransactionAttemptStore();
        var attempts = new TransactionAttemptService(attemptStore, clock, ids);
        var idempotency = new IdempotencyService(new PostgresIdempotencyStore(), clock, ids);
        var ledger = new LedgerPostingService(new PostgresLedgerStore(new ReferenceDataStore()), new PostgresOutboxWriter(), clock, ids);
        var configuration = new ConfigurationService(new PostgresConfigVersionStore(), clock);
        var routingStore = new PostgresRoutingStore();
        var finalization = new TransactionFinalizationService(sessions, transactions, ledger, new PostgresOutboxWriter(), clock, ids, attemptStore, attempts);

        Service = new TransactionProcessingService(
            sessions,
            transactions,
            attemptStore,
            attempts,
            idempotency,
            ledger,
            new RoutingService(configuration, routingStore, clock),
            new FeeResolver(configuration, new PostgresFeeRuleReader()),
            new PostgresReferenceDataReader(),
            new ReversalService(transactions, idempotency, routingStore, new PostgresOutboxWriter(), clock, ids),
            new ChildTransactionService(transactions, idempotency, routingStore, new PostgresOutboxWriter(), clock, ids),
            finalization,
            new ProviderRequestFactory(TestPolicy),
            new ProviderInvoker(Registry, clock, TestPolicy),
            new PostgresTransactionQuery(),
            clock,
            TransactionProcessingOptions.Default);
    }

    public CoreHarness Core { get; }

    public InProcessProviderAdapterRegistry Registry { get; } = new();

    public TransactionProcessingService Service { get; }

    /// <summary>Creates a scenario; providers get routes (wildcard type) in priority order and every capability.</summary>
    public async Task<Scenario> NewScenario(decimal balance = 1_000_000m, int providers = 1)
    {
        var merchant = Guid.CreateVersion7();
        var channel = Guid.CreateVersion7();
        var wallet = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        var productCode = $"PRD-{product:N}"[..30];
        await Execute(
            """
            INSERT INTO core.merchants VALUES (@merchant, @merchantCode, 'Processing Test Merchant', 'ACTIVE', now(), now());
            INSERT INTO core.channels VALUES (@channel, @merchant, 'API', 'REST', 'SIGNED_API', 'ACTIVE', now(), now());
            INSERT INTO core.products VALUES (@product, @productCode, 'Processing Test Product', 'BILLER', 'ACTIVE', NULL, now(), now());
            INSERT INTO ledger.wallets
                (wallet_id, merchant_id, product_id, currency_definition_id, ledger_balance, available_balance,
                 reserved_balance, status, created_at, updated_at)
            VALUES (@wallet, @merchant, NULL, @currency, 0, 0, 0, 'ACTIVE', now(), now());
            """,
            new { merchant, merchantCode = $"MRC-{merchant:N}", channel, product, productCode, wallet, currency = _db.Seed.IdrV1CurrencyDefinitionId });

        if (balance > 0)
        {
            await using var session = await Core.Session();
            CoreHarness.Ok(await Core.Ledger.PostTopUpAsync(
                session, new TopUpRequest(new WalletId(wallet), $"TP-{Guid.NewGuid():N}", CoreHarness.Rp(balance), null, LedgerActor.System)));
            await session.CommitAsync();
        }

        var list = new List<(ProviderId, ScriptedProviderAdapter)>();
        for (short priority = 1; priority <= providers; priority++)
        {
            var id = Guid.CreateVersion7();
            await Execute(
                """
                INSERT INTO integration.providers VALUES (@id, @code, 'Processing Test Provider', 'BANK', 'ransys-adapter-test', 'REST', 'ACTIVE', now(), now());
                INSERT INTO config.routing_routes VALUES (@route, @config, @product, NULL, @id, @priority, true, now());
                INSERT INTO integration.provider_operational_state VALUES (@id, true, NULL, NULL, 'HEALTHY', 'CLOSED', now(), now());
                INSERT INTO integration.provider_capabilities SELECT @id, c, true, '{}'::jsonb, now() FROM unnest(@capabilities) AS c;
                """,
                new
                {
                    id,
                    code = $"PRV-{id:N}"[..30],
                    route = Guid.CreateVersion7(),
                    config = _db.Seed.ConfigVersionId,
                    product,
                    priority,
                    capabilities = ProviderCapabilities.All.ToArray(),
                });

            var adapter = new ScriptedProviderAdapter();
            Registry.Register(new ProviderId(id), adapter);
            list.Add((new ProviderId(id), adapter));
        }

        return new Scenario(new MerchantId(merchant), new ChannelId(channel), new WalletId(wallet), new ProductId(product), productCode, list);
    }

    /// <summary>The single active FEE configuration version of the test database (created on first use).</summary>
    public async Task<Guid> FeeConfigVersion()
    {
        await Execute(
            """
            INSERT INTO config.config_versions
                (config_version_id, config_domain, version_no, status, effective_from, created_by, created_at, activated_at)
            VALUES (@id, 'FEE', 1, 'ACTIVE', @from, @creator, now(), now())
            ON CONFLICT (config_domain, version_no) DO NOTHING
            """,
            new { id = Guid.CreateVersion7(), from = TestSeed.SeedTime.UtcDateTime, creator = _db.Seed.MerchantId });
        return await Core.Query<Guid>("SELECT config_version_id FROM config.config_versions WHERE config_domain = 'FEE' AND version_no = 1", new { });
    }

    public async Task AddFeeRule(
        Scenario scenario, string transactionType, string feeType, decimal value, decimal? min = null, decimal? max = null, bool merchantSpecific = false)
    {
        var version = await FeeConfigVersion();
        await Execute(
            """
            INSERT INTO config.fee_rules VALUES
                (@id, @version, @merchant, @product, @type, @feeType, @value, @min, @max, @currency, @from, NULL, now())
            """,
            new
            {
                id = Guid.CreateVersion7(),
                version,
                merchant = merchantSpecific ? scenario.Merchant.Value : (Guid?)null,
                product = scenario.Product.Value,
                type = transactionType,
                feeType,
                value,
                min,
                max,
                currency = _db.Seed.IdrV1CurrencyDefinitionId,
                from = TestSeed.SeedTime.UtcDateTime,
            });
    }

    public async Task<(decimal Ledger, decimal Available, decimal Reserved)> Balances(WalletId wallet) =>
        await Core.Query<(decimal, decimal, decimal)>(
            "SELECT ledger_balance, available_balance, reserved_balance FROM ledger.wallets WHERE wallet_id = @id", new { id = wallet.Value });

    public Task<int> Journals(string postingKey) =>
        Core.Query<int>("SELECT count(*)::int FROM ledger.ledger_transactions WHERE posting_key = @postingKey", new { postingKey });

    public async Task Execute(string sql, object parameters)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(sql, parameters);
    }
}
