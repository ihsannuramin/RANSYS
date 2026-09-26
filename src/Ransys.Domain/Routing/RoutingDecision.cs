using Ransys.Domain.Common;

namespace Ransys.Domain.Routing;

/// <summary>
/// Routing audit for one transaction (Canonical Data Model §25–27, Architecture Spec §15):
/// initial vs current provider, rule version, failover count and reason.
/// <para>
/// This type records failovers; it does not decide whether one is safe. Failover is only permitted when
/// the previous attempt proves the request was not sent (ADR-005), which the Transaction aggregate enforces.
/// </para>
/// </summary>
public sealed record RoutingDecision
{
    /// <summary>Failover reasons are reason codes (ADR-013 <c>failover_reason varchar(64)</c>).</summary>
    public const int MaxFailoverReasonLength = 64;

    private RoutingDecision(
        ProviderReference initialProvider,
        ProviderReference currentProvider,
        long ruleVersion,
        int failoverCount,
        string? failoverReason,
        DateTimeOffset decisionTimestamp)
    {
        InitialProvider = initialProvider;
        CurrentProvider = currentProvider;
        RuleVersion = ruleVersion;
        FailoverCount = failoverCount;
        FailoverReason = failoverReason;
        DecisionTimestamp = decisionTimestamp;
    }

    public ProviderReference InitialProvider { get; }

    public ProviderReference CurrentProvider { get; }

    /// <summary>Routing rule/config version used for this decision.</summary>
    public long RuleVersion { get; }

    public int FailoverCount { get; }

    /// <summary>Reason of the most recent failover; required whenever <see cref="FailoverCount"/> &gt; 0.</summary>
    public string? FailoverReason { get; }

    public DateTimeOffset DecisionTimestamp { get; }

    public static Result<RoutingDecision> Initial(
        ProviderReference provider, long ruleVersion, DateTimeOffset decidedAt) =>
        Create(provider, provider, ruleVersion, failoverCount: 0, failoverReason: null, decidedAt);

    /// <summary>Validates a complete decision, e.g. when rehydrating from persistence.</summary>
    public static Result<RoutingDecision> Create(
        ProviderReference initialProvider,
        ProviderReference currentProvider,
        long ruleVersion,
        int failoverCount,
        string? failoverReason,
        DateTimeOffset decisionTimestamp)
    {
        ArgumentNullException.ThrowIfNull(initialProvider);
        ArgumentNullException.ThrowIfNull(currentProvider);

        if (ruleVersion <= 0)
        {
            return Invalid("Routing rule version must be positive.");
        }

        if (failoverCount < 0)
        {
            return Invalid("Failover count cannot be negative.");
        }

        if (failoverCount == 0 && (initialProvider != currentProvider || failoverReason is not null))
        {
            return Invalid("Without failover the current provider must equal the initial provider and no reason is recorded.");
        }

        if (failoverCount > 0 && string.IsNullOrWhiteSpace(failoverReason))
        {
            return Invalid("A failover reason is required when failover occurred.");
        }

        if (failoverReason?.Length > MaxFailoverReasonLength)
        {
            return Invalid($"Failover reason exceeds {MaxFailoverReasonLength} characters.");
        }

        return new RoutingDecision(
            initialProvider, currentProvider, ruleVersion, failoverCount, failoverReason, decisionTimestamp);
    }

    /// <summary>Records a pre-send failover to <paramref name="nextProvider"/>.</summary>
    public Result<RoutingDecision> FailoverTo(ProviderReference nextProvider, string? reason, DateTimeOffset decidedAt)
    {
        ArgumentNullException.ThrowIfNull(nextProvider);

        if (nextProvider.ProviderId == CurrentProvider.ProviderId)
        {
            return RansysError.Validation(
                ErrorCodes.RoutingFailoverToSameProvider, "Failover target must differ from the current provider.", "provider");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Invalid("A failover reason is required.");
        }

        if (reason.Length > MaxFailoverReasonLength)
        {
            return Invalid($"Failover reason exceeds {MaxFailoverReasonLength} characters.");
        }

        if (decidedAt < DecisionTimestamp)
        {
            return Invalid("Failover cannot be dated before the previous routing decision.");
        }

        return new RoutingDecision(InitialProvider, nextProvider, RuleVersion, FailoverCount + 1, reason, decidedAt);
    }

    private static RansysError Invalid(string message) =>
        RansysError.Validation(ErrorCodes.RoutingInvalidDecision, message, "routing");
}
