using Dapper;
using Ransys.Configuration;
using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Monetary;
using Ransys.Domain.Routing;
using Ransys.Domain.Transactions;
using Ransys.Infrastructure;
using Ransys.Persistence.PostgreSql;
using Ransys.Persistence.PostgreSql.Configuration;
using Ransys.Persistence.PostgreSql.Routing;
using Ransys.Routing;
using Ransys.Testing.PostgreSql;

namespace Ransys.IntegrationTests.Routing;

/// <summary>
/// Priority routing on a real PostgreSQL server (main.md §19): active ROUTING configuration version, provider
/// master, operational state and capabilities. Each test uses its own product and providers.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RoutingServiceTests(PostgresDatabaseFixture db)
{
    private readonly RoutingService _routing = new(
        new ConfigurationService(new PostgresConfigVersionStore(), new SystemClock()), new PostgresRoutingStore(), new SystemClock());

    [Fact]
    public async Task Selects_primary_from_the_active_routing_version()
    {
        var product = await NewProduct();
        var primary = await NewProvider(product, priority: 1);
        await NewProvider(product, priority: 2);

        var result = await Route(product);

        Assert.Equal(primary, result.Value.SelectedProvider.ProviderId.Value);
        Assert.Equal(db.Seed.ConfigVersionId, result.Value.ConfigVersionId);
        Assert.Equal(1, result.Value.RuleVersion);
    }

    [Fact]
    public async Task Manually_disabled_or_circuit_open_primary_falls_back_to_secondary()
    {
        var product = await NewProduct();
        var primary = await NewProvider(product, priority: 1);
        var secondary = await NewProvider(product, priority: 2);

        await Execute("UPDATE integration.provider_operational_state SET manual_enabled = false, manual_disable_reason = 'maintenance' WHERE provider_id = @primary", new { primary });
        var manual = await Route(product);

        await Execute("UPDATE integration.provider_operational_state SET manual_enabled = true, circuit_state = 'OPEN' WHERE provider_id = @primary", new { primary });
        var circuit = await Route(product);

        Assert.Equal(secondary, manual.Value.SelectedProvider.ProviderId.Value);
        Assert.Equal(RoutingExclusionReason.ManualDisabled, Assert.Single(manual.Value.Exclusions).Reason);
        Assert.Equal(secondary, circuit.Value.SelectedProvider.ProviderId.Value);
        Assert.Equal(RoutingExclusionReason.CircuitOpen, Assert.Single(circuit.Value.Exclusions).Reason);
    }

    [Fact]
    public async Task Provider_without_the_capability_or_operational_state_is_skipped()
    {
        var product = await NewProduct();
        await NewProvider(product, priority: 1, capabilities: [ProviderCapabilities.Inquiry]);
        await NewProvider(product, priority: 2, withOperationalState: false);
        var third = await NewProvider(product, priority: 3);

        var result = await Route(product);

        Assert.Equal(third, result.Value.SelectedProvider.ProviderId.Value);
        Assert.Equal(
            [RoutingExclusionReason.CapabilityUnsupported, RoutingExclusionReason.OperationalStateMissing],
            result.Value.Exclusions.Select(e => e.Reason));
    }

    [Fact]
    public async Task All_providers_unavailable_is_no_route_available()
    {
        var product = await NewProduct();
        var a = await NewProvider(product, priority: 1);
        var b = await NewProvider(product, priority: 2);
        await Execute("UPDATE integration.provider_operational_state SET health_state = 'UNHEALTHY' WHERE provider_id = ANY(@ids)", new { ids = new[] { a, b } });

        var result = await Route(product);

        Assert.Equal(ErrorCodes.NoRouteAvailable, result.Error.Code);
    }

    [Fact]
    public async Task Failover_request_excludes_the_provider_that_failed_before_send()
    {
        var product = await NewProduct();
        var primary = await NewProvider(product, priority: 1);
        var secondary = await NewProvider(product, priority: 2);

        var result = await Route(product, excluded: [new ProviderId(primary)]);

        Assert.Equal(secondary, result.Value.SelectedProvider.ProviderId.Value);
        Assert.Equal(RoutingExclusionReason.PreviousSafeFailure, Assert.Single(result.Value.Exclusions).Reason);
    }

    [Fact]
    public async Task Type_specific_routes_override_wildcard_routes()
    {
        var product = await NewProduct();
        await NewProvider(product, priority: 1, transactionType: null);        // wildcard
        var specific = await NewProvider(product, priority: 2, transactionType: "PAYMENT");

        var payment = await Route(product);
        var inquiry = await Route(product, TransactionType.Inquiry);

        Assert.Equal(specific, payment.Value.SelectedProvider.ProviderId.Value);
        Assert.Empty(payment.Value.Exclusions); // the wildcard route is not even a candidate
        Assert.True(inquiry.IsSuccess); // no INQUIRY-specific route: wildcard applies
    }

    [Fact]
    public async Task Missing_active_configuration_fails_closed()
    {
        var configuration = new ConfigurationService(new PostgresConfigVersionStore(), new SystemClock());
        await using var session = await PostgresSession.BeginAsync(db.DataSource);

        var result = await configuration.GetActiveAsync(session, $"NO_SUCH_DOMAIN_{Guid.NewGuid():N}");

        Assert.Equal(ErrorCodes.ConfigurationNotAvailable, result.Error.Code);
    }

    private async Task<Result<RoutingResult>> Route(Guid product, TransactionType type = TransactionType.Payment, ProviderId[]? excluded = null)
    {
        await using var session = await PostgresSession.BeginAsync(db.DataSource);
        var money = Money.Create(100_000m, CoreHarness.Idr).Value;
        return await _routing.RouteAsync(session, new RoutingRequest(
            new TransactionId(Guid.CreateVersion7()), type, new ProductId(product), new MerchantId(db.Seed.MerchantId),
            new ChannelId(db.Seed.ChannelId), money, excluded ?? []));
    }

    private async Task<Guid> NewProduct()
    {
        var id = Guid.CreateVersion7();
        await Execute(
            "INSERT INTO core.products VALUES (@id, @code, 'Routing Test Product', 'BILLER', 'ACTIVE', NULL, now(), now())",
            new { id, code = $"PRD-{id:N}" });
        return id;
    }

    /// <summary>Provider with operational state (healthy, closed circuit), capabilities and a route in the seed ROUTING version.</summary>
    private async Task<Guid> NewProvider(
        Guid product, short priority, string[]? capabilities = null, bool withOperationalState = true, string? transactionType = "PAYMENT")
    {
        var id = Guid.CreateVersion7();
        await Execute(
            """
            INSERT INTO integration.providers VALUES (@id, @code, 'Routing Test Provider', 'BANK', 'ransys-adapter-test', 'REST', 'ACTIVE', now(), now());
            INSERT INTO config.routing_routes VALUES (@route, @config, @product, @transactionType, @id, @priority, true, now());
            """,
            new
            {
                id,
                code = $"PRV-{id:N}"[..30],
                route = Guid.CreateVersion7(),
                config = db.Seed.ConfigVersionId,
                product,
                transactionType,
                priority,
            });

        if (withOperationalState)
        {
            await Execute(
                "INSERT INTO integration.provider_operational_state VALUES (@id, true, NULL, NULL, 'HEALTHY', 'CLOSED', now(), now())",
                new { id });
        }

        foreach (var capability in capabilities ?? [ProviderCapabilities.Payment, ProviderCapabilities.Inquiry, ProviderCapabilities.StatusCheck])
        {
            await Execute(
                "INSERT INTO integration.provider_capabilities VALUES (@id, @capability, true, '{}'::jsonb, now())",
                new { id, capability });
        }

        return id;
    }

    private async Task Execute(string sql, object parameters)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(sql, parameters);
    }
}
