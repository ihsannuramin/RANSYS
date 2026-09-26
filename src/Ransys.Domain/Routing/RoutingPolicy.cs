using Ransys.Domain.Common;
using Ransys.Domain.Monetary;
using Ransys.Domain.Transactions;

namespace Ransys.Domain.Routing;

/// <summary>Provider capability codes (Architecture Spec §17; <c>integration.provider_capabilities.capability_code</c>).</summary>
public static class ProviderCapabilities
{
    public const string Inquiry = "supports_inquiry";
    public const string Payment = "supports_payment";
    public const string Purchase = "supports_purchase";
    public const string StatusCheck = "supports_status_check";
    public const string Reversal = "supports_reversal";
    public const string Refund = "supports_refund";
    public const string Advice = "supports_advice";
    public const string Callback = "supports_callback";
    public const string BalanceCheck = "supports_balance_check";
    public const string Reconciliation = "supports_reconciliation";
    public const string SettlementFile = "supports_settlement_file";

    // TODO / Architecture Decision Required: Architecture Spec §17 defines no capability for TRANSFER or VOID.
    // These codes follow the same convention; providers must declare them explicitly (fail closed).
    public const string Transfer = "supports_transfer";
    public const string Void = "supports_void";

    /// <summary>Capability a provider needs to receive a transaction of <paramref name="type"/>; null if not routable.</summary>
    public static string? RequiredFor(TransactionType type) => type switch
    {
        TransactionType.Inquiry => Inquiry,
        TransactionType.Payment => Payment,
        TransactionType.Purchase => Purchase,
        TransactionType.Transfer => Transfer,
        TransactionType.Refund => Refund,
        TransactionType.Reversal => Reversal,
        TransactionType.Void => Void,
        TransactionType.Advice => Advice,
        TransactionType.BalanceInquiry => BalanceCheck,
        TransactionType.StatusCheck => StatusCheck,

        // Settlement, top-up and adjustment are internal flows, never routed to a provider.
        _ => null,
    };
}

public enum ProviderHealth
{
    Healthy,
    Degraded,
    Unhealthy,
}

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen,
}

/// <summary>Operational read model of one provider (Canonical Data Model §147–148). Manual disable is separate from health.</summary>
public sealed record ProviderOperationalState(
    bool ManualEnabled,
    DateTimeOffset? ManualDisabledUntil,
    ProviderHealth Health,
    CircuitState Circuit)
{
    /// <summary>A manual disable with an end time expires by itself; without one it lasts until re-enabled.</summary>
    public bool IsManuallyDisabled(DateTimeOffset now) =>
        !ManualEnabled && (ManualDisabledUntil is null || ManualDisabledUntil > now);
}

/// <summary>One configured route to a provider, with everything needed to decide eligibility.</summary>
public sealed record ProviderRouteCandidate(
    ProviderReference Provider,
    short Priority,
    bool RouteEnabled,
    bool ProviderActive,
    ProviderOperationalState? Operational,
    IReadOnlySet<string> EnabledCapabilities);

/// <summary>Routing input (Canonical Data Model §77). Carries no customer PII.</summary>
public sealed record RoutingRequest(
    TransactionId TransactionId,
    TransactionType TransactionType,
    ProductId ProductId,
    MerchantId MerchantId,
    ChannelId ChannelId,
    Money Amount,
    IReadOnlyCollection<ProviderId> ExcludedProviders);

/// <summary>Why a provider was not eligible (Canonical Data Model §79).</summary>
public enum RoutingExclusionReason
{
    RouteDisabled,
    ProviderInactive,
    OperationalStateMissing,
    ManualDisabled,
    CircuitOpen,
    Unhealthy,
    CapabilityUnsupported,
    PreviousSafeFailure,
}

public sealed record RoutingExclusion(ProviderReference Provider, short Priority, RoutingExclusionReason Reason);

/// <summary>Routing output (Canonical Data Model §78). Baseline routing has no weight.</summary>
public sealed record RoutingResult(
    ProviderReference SelectedProvider,
    short Priority,
    Guid ConfigVersionId,
    long RuleVersion,
    string Reason,
    DateTimeOffset EvaluatedAt,
    IReadOnlyList<RoutingExclusion> Exclusions)
{
    /// <summary>Initial routing decision to capture on the transaction (<see cref="Transaction.BeginProcessing"/>).</summary>
    public Result<RoutingDecision> ToInitialDecision() => RoutingDecision.Initial(SelectedProvider, RuleVersion, EvaluatedAt);
}

/// <summary>
/// Baseline priority routing (Architecture Spec §19–20, State Transition Matrix §66–67): primary, secondary,
/// then further fallbacks in ascending priority, skipping providers that are disabled, manually disabled,
/// circuit-open, unhealthy, missing the required capability, or excluded by the caller (a provider that already
/// failed safely before send). Weighted, least-cost and smart routing are out of scope (main.md §19).
/// <para>
/// DEGRADED providers and HALF_OPEN circuits stay eligible; limiting half-open probe traffic belongs to the circuit
/// breaker, which is not part of Phase 1 routing.
/// </para>
/// </summary>
public static class RoutingPolicy
{
    public static Result<RoutingResult> Select(
        RoutingRequest request,
        IReadOnlyCollection<ProviderRouteCandidate> candidates,
        Guid configVersionId,
        long ruleVersion,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidates);

        var capability = ProviderCapabilities.RequiredFor(request.TransactionType);
        if (capability is null)
        {
            return NoRoute(request, [], $"{CanonicalCodes.TransactionType.ToCode(request.TransactionType)} is not routable to a provider");
        }

        var exclusions = new List<RoutingExclusion>();
        ProviderRouteCandidate? selected = null;
        foreach (var candidate in candidates.OrderBy(c => c.Priority).ThenBy(c => c.Provider.ProviderCode, StringComparer.Ordinal))
        {
            var reason = Exclude(candidate, capability, request.ExcludedProviders, now);
            if (reason is { } r)
            {
                exclusions.Add(new RoutingExclusion(candidate.Provider, candidate.Priority, r));
            }
            else if (selected is null)
            {
                selected = candidate;
            }
        }

        if (selected is null)
        {
            return NoRoute(request, exclusions, "no eligible provider");
        }

        var position = exclusions.Count(e => e.Priority < selected.Priority) == 0 ? "PRIMARY_ELIGIBLE" : "FALLBACK";
        return new RoutingResult(
            selected.Provider, selected.Priority, configVersionId, ruleVersion, $"{position}_PRIORITY_{selected.Priority}", now, exclusions);
    }

    private static RoutingExclusionReason? Exclude(
        ProviderRouteCandidate candidate, string capability, IReadOnlyCollection<ProviderId> excluded, DateTimeOffset now)
    {
        if (excluded.Contains(candidate.Provider.ProviderId))
        {
            return RoutingExclusionReason.PreviousSafeFailure;
        }

        if (!candidate.RouteEnabled)
        {
            return RoutingExclusionReason.RouteDisabled;
        }

        if (!candidate.ProviderActive)
        {
            return RoutingExclusionReason.ProviderInactive;
        }

        // Unknown operational state is not evidence that the provider can take traffic: fail closed.
        if (candidate.Operational is not { } operational)
        {
            return RoutingExclusionReason.OperationalStateMissing;
        }

        if (operational.IsManuallyDisabled(now))
        {
            return RoutingExclusionReason.ManualDisabled;
        }

        if (operational.Circuit == CircuitState.Open)
        {
            return RoutingExclusionReason.CircuitOpen;
        }

        if (operational.Health == ProviderHealth.Unhealthy)
        {
            return RoutingExclusionReason.Unhealthy;
        }

        return candidate.EnabledCapabilities.Contains(capability) ? null : RoutingExclusionReason.CapabilityUnsupported;
    }

    private static RansysError NoRoute(RoutingRequest request, List<RoutingExclusion> exclusions, string detail)
    {
        var summary = exclusions.Count == 0
            ? "no configured route"
            : string.Join(", ", exclusions.Select(e => $"{e.Provider.ProviderCode}={e.Reason}"));
        return new RansysError(
            ErrorCodes.NoRouteAvailable,
            ErrorCategory.Routing,
            $"No route for transaction {request.TransactionId}: {detail} ({summary}).");
    }
}
