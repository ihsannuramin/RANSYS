using Ransys.Application;
using Ransys.Configuration;
using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Routing;
using Ransys.Domain.Transactions;

namespace Ransys.Routing;

/// <summary>Persistence port for routes and the provider read models routing needs.</summary>
public interface IRoutingStore
{
    /// <summary>
    /// Route candidates of one routing configuration version for a product and transaction type. Routes defined for
    /// the specific type take precedence; wildcard routes (<c>transaction_type IS NULL</c>) apply only when none exist.
    /// </summary>
    Task<Result<IReadOnlyList<ProviderRouteCandidate>>> GetCandidatesAsync(
        IDatabaseSession session, Guid configVersionId, ProductId productId, TransactionType transactionType, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves the provider for a transaction from the active ROUTING configuration version (main.md §19).
/// The rule version used is returned so it can be captured on the transaction (State Transition Matrix §68).
/// </summary>
public sealed class RoutingService(ConfigurationService configuration, IRoutingStore store, IClock clock)
{
    /// <summary>
    /// Selects the highest-priority eligible provider. For a pre-send failover, pass the providers already tried in
    /// <see cref="RoutingRequest.ExcludedProviders"/>; whether failover is safe is decided by the Transaction aggregate
    /// (<see cref="Transaction.RecordFailover"/>), never here.
    /// </summary>
    public async Task<Result<RoutingResult>> RouteAsync(
        IDatabaseSession session, RoutingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var version = await configuration.GetActiveAsync(session, ConfigDomains.Routing, cancellationToken);
        if (version.IsFailure)
        {
            return version.Error;
        }

        var candidates = await store.GetCandidatesAsync(
            session, version.Value.Id, request.ProductId, request.TransactionType, cancellationToken);
        if (candidates.IsFailure)
        {
            return candidates.Error;
        }

        return RoutingPolicy.Select(request, candidates.Value, version.Value.Id, version.Value.VersionNo, clock.UtcNow);
    }
}
