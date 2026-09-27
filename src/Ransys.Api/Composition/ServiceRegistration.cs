using Npgsql;
using Ransys.Api.Security;
using Ransys.Application;
using Ransys.Configuration;
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
using Ransys.TransactionCore;
using Ransys.TransactionCore.Attempts;
using Ransys.TransactionCore.Children;
using Ransys.TransactionCore.Fees;
using Ransys.TransactionCore.Finalization;
using Ransys.TransactionCore.Idempotency;
using Ransys.TransactionCore.Processing;
using Ransys.TransactionCore.Providers;
using Ransys.TransactionCore.Reversal;

namespace Ransys.Api.Composition;

/// <summary>Configuration section <c>Ransys:ProviderCall</c>: the Core provider call budget (ProviderCallPolicy).</summary>
public sealed class ProviderCallOptions
{
    public const string Section = "Ransys:ProviderCall";

    public int ConnectTimeoutMs { get; set; } = (int)ProviderCallPolicy.Default.ConnectTimeout.TotalMilliseconds;

    public int ReadTimeoutMs { get; set; } = (int)ProviderCallPolicy.Default.ReadTimeout.TotalMilliseconds;
}

/// <summary>
/// Composition of the Transaction Core service graph on PostgreSQL (the same graph as
/// <c>tests/Ransys.IntegrationTests/Processing/ProcessingHarness</c>). All stores and services are stateless and
/// thread-safe, so they are singletons; every unit of work opens its own <see cref="IDatabaseSession"/>.
/// </summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddRansysTransactionProcessing(this IServiceCollection services, string connectionString, IConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(configuration);

        var call = configuration.GetSection(ProviderCallOptions.Section).Get<ProviderCallOptions>() ?? new ProviderCallOptions();
        var policy = new ProviderCallPolicy(TimeSpan.FromMilliseconds(call.ConnectTimeoutMs), TimeSpan.FromMilliseconds(call.ReadTimeoutMs));

        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<IDatabaseSessionFactory, PostgresSessionFactory>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdGenerator, UuidV7IdGenerator>();

        // Stores.
        services.AddSingleton<ReferenceDataStore>();
        services.AddSingleton<ITransactionRepository>(sp => new TransactionStore(sp.GetRequiredService<ReferenceDataStore>()));
        services.AddSingleton<ITransactionAttemptStore, PostgresTransactionAttemptStore>();
        services.AddSingleton<IIdempotencyStore, PostgresIdempotencyStore>();
        services.AddSingleton<ILedgerStore>(sp => new PostgresLedgerStore(sp.GetRequiredService<ReferenceDataStore>()));
        services.AddSingleton<IOutboxWriter, PostgresOutboxWriter>();
        services.AddSingleton<IConfigVersionStore, PostgresConfigVersionStore>();
        services.AddSingleton<IRoutingStore, PostgresRoutingStore>();
        services.AddSingleton<IFeeRuleReader, PostgresFeeRuleReader>();
        services.AddSingleton<IReferenceDataReader, PostgresReferenceDataReader>();
        services.AddSingleton<ITransactionQuery, PostgresTransactionQuery>();
        services.AddSingleton<IChannelDirectory, PostgresChannelDirectory>();

        // Services.
        services.AddSingleton<TransactionAttemptService>();
        services.AddSingleton<IdempotencyService>();
        services.AddSingleton<ILedgerPostingService, LedgerPostingService>();
        services.AddSingleton<ConfigurationService>();
        services.AddSingleton<RoutingService>();
        services.AddSingleton<FeeResolver>();
        services.AddSingleton<ReversalService>();
        services.AddSingleton<ChildTransactionService>();
        services.AddSingleton<TransactionFinalizationService>();

        // ADR-005 crash recovery (R5): closes outcome-less attempts and moves the transaction IN_DOUBT. No hosted
        // schedule wires this up yet (README TODO); this registration only makes it DI-resolvable.
        services.AddSingleton<AttemptRecoveryService>();

        // Provider gateway. No real adapters exist yet: the in-process registry is empty in the API host, so a routed
        // provider without a binding is NOT_SENT (never sent, safe failover) and ends FAILED 5001 when none is left.
        // TODO: register gRPC adapter clients (GrpcProviderAdapterClient) per configured provider endpoint.
        services.AddSingleton(policy);
        services.AddSingleton<InProcessProviderAdapterRegistry>();
        services.AddSingleton<IProviderAdapterResolver>(sp => sp.GetRequiredService<InProcessProviderAdapterRegistry>());
        services.AddSingleton<ProviderRequestFactory>();
        services.AddSingleton<ProviderInvoker>();

        services.AddSingleton(TransactionProcessingOptions.Default);
        services.AddSingleton<TransactionProcessingService>();
        return services;
    }

    /// <summary>
    /// ADR-022: production default is <see cref="FailClosedRequestAuthenticationService"/> (every request 401). The
    /// development authenticator is registered only in Development/Test with <c>Ransys:Auth:AllowDevelopmentAuthentication</c>;
    /// the flag in any other environment stops the host at startup.
    /// </summary>
    public static IServiceCollection AddRansysRequestAuthentication(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var options = configuration.GetSection(ApiAuthOptions.Section).Get<ApiAuthOptions>() ?? new ApiAuthOptions();
        if (options.AllowDevelopmentAuthentication && !ApiAuthOptions.IsDevelopmentAuthenticationEnvironment(environment))
        {
            throw new InvalidOperationException(
                $"{ApiAuthOptions.Section}:AllowDevelopmentAuthentication is set in environment '{environment.EnvironmentName}'. " +
                "Development authentication is allowed only in Development or Test (ADR-022).");
        }

        if (options.TimestampToleranceSeconds is <= 0 or > 3600)
        {
            throw new InvalidOperationException($"{ApiAuthOptions.Section}:TimestampToleranceSeconds must be between 1 and 3600.");
        }

        services.AddSingleton(options);
        services.AddSingleton<ISignatureVerifier, FailClosedSignatureVerifier>();
        if (options.AllowDevelopmentAuthentication)
        {
            services.AddSingleton<IReplayProtectionService, InMemoryReplayProtectionService>();
            services.AddSingleton<IRequestAuthenticationService, DevelopmentRequestAuthenticationService>();
        }
        else
        {
            services.AddSingleton<IRequestAuthenticationService, FailClosedRequestAuthenticationService>();
        }

        return services;
    }
}
