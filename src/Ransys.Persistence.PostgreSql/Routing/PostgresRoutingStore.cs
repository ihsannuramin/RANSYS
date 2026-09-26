using Dapper;
using Ransys.Application;
using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Routing;
using Ransys.Domain.Transactions;
using Ransys.Routing;

namespace Ransys.Persistence.PostgreSql.Routing;

/// <summary>
/// Reads <c>config.routing_routes</c> with the provider master, operational state and enabled capabilities
/// (<c>integration.*</c>). Read-only: routing never mutates provider state.
/// </summary>
public sealed class PostgresRoutingStore : IRoutingStore
{
    static PostgresRoutingStore() => DbValues.EnsureConfigured();

    public async Task<Result<IReadOnlyList<ProviderRouteCandidate>>> GetCandidatesAsync(
        IDatabaseSession session, Guid configVersionId, ProductId productId, TransactionType transactionType, CancellationToken cancellationToken = default)
    {
        var s = PostgresSessionCast.From(session);
        var rows = (await s.Connection.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT r.provider_id, p.provider_code, p.adapter_service_name, r.priority, r.enabled AS route_enabled,
                   r.transaction_type, p.status AS provider_status,
                   os.provider_id IS NOT NULL AS has_operational_state, os.manual_enabled, os.manual_disabled_until,
                   os.health_state, os.circuit_state,
                   COALESCE((SELECT array_agg(c.capability_code) FROM integration.provider_capabilities c
                             WHERE c.provider_id = r.provider_id AND c.enabled), '{}') AS capabilities
            FROM config.routing_routes r
            JOIN integration.providers p ON p.provider_id = r.provider_id
            LEFT JOIN integration.provider_operational_state os ON os.provider_id = r.provider_id
            WHERE r.config_version_id = @ConfigVersionId
              AND r.product_id = @ProductId
              AND (r.transaction_type = @TransactionType OR r.transaction_type IS NULL)
            ORDER BY r.priority
            """,
            new
            {
                ConfigVersionId = configVersionId,
                ProductId = productId.Value,
                TransactionType = CanonicalCodes.TransactionType.ToCode(transactionType),
            },
            s.Transaction, cancellationToken: cancellationToken))).ToList();

        // Specific routes for the transaction type take precedence over wildcard routes.
        var effective = rows.Any(r => r.TransactionType is not null) ? rows.Where(r => r.TransactionType is not null) : rows;

        var candidates = new List<ProviderRouteCandidate>();
        foreach (var row in effective)
        {
            var provider = ProviderReference.Create(new ProviderId(row.ProviderId), row.ProviderCode, row.AdapterServiceName);
            if (provider.IsFailure)
            {
                return Corrupt(row.ProviderId);
            }

            ProviderOperationalState? operational = null;
            if (row.HasOperationalState)
            {
                if (!CanonicalCodes.ProviderHealth.TryParse(row.HealthState, out var health)
                    || !CanonicalCodes.CircuitState.TryParse(row.CircuitState, out var circuit))
                {
                    return Corrupt(row.ProviderId);
                }

                operational = new ProviderOperationalState(
                    row.ManualEnabled ?? false, DbValues.FromDb(row.ManualDisabledUntil), health, circuit);
            }

            candidates.Add(new ProviderRouteCandidate(
                provider.Value,
                row.Priority,
                row.RouteEnabled,
                string.Equals(row.ProviderStatus, "ACTIVE", StringComparison.Ordinal),
                operational,
                row.Capabilities.ToHashSet(StringComparer.Ordinal)));
        }

        return candidates;
    }

    public async Task<bool> ProviderHasCapabilityAsync(
        IDatabaseSession session, ProviderId providerId, string capability, CancellationToken cancellationToken = default)
    {
        var s = PostgresSessionCast.From(session);
        return await s.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS (SELECT 1 FROM integration.provider_capabilities
                           WHERE provider_id = @ProviderId AND capability_code = @Capability AND enabled)
            """,
            new { ProviderId = providerId.Value, Capability = capability }, s.Transaction, cancellationToken: cancellationToken));
    }

    private static RansysError Corrupt(Guid providerId) =>
        new(ErrorCodes.PersistedStateInvalid, ErrorCategory.Internal, $"Provider {providerId} routing data is inconsistent.");

    private sealed class Row
    {
        public Guid ProviderId { get; init; }

        public string ProviderCode { get; init; } = "";

        public string AdapterServiceName { get; init; } = "";

        public short Priority { get; init; }

        public bool RouteEnabled { get; init; }

        public string? TransactionType { get; init; }

        public string ProviderStatus { get; init; } = "";

        public bool HasOperationalState { get; init; }

        public bool? ManualEnabled { get; init; }

        public DateTime? ManualDisabledUntil { get; init; }

        public string? HealthState { get; init; }

        public string? CircuitState { get; init; }

        public string[] Capabilities { get; init; } = [];
    }
}
