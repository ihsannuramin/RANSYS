using Ransys.Domain.Common;
using Ransys.Domain.Routing;
using Ransys.Domain.Transactions;
using static Ransys.Domain.Tests.TestData;

namespace Ransys.Domain.Tests.Routing;

public sealed class RoutingPolicyTests
{
    private static readonly Guid ConfigVersion = Guid.CreateVersion7();
    private static readonly ProviderOperationalState Healthy = new(true, null, ProviderHealth.Healthy, CircuitState.Closed);

    private static readonly ProviderReference ProviderC =
        ProviderReference.Create(new ProviderId(Guid.CreateVersion7()), "BANK_C", "ransys-adapter-bank-c").Value;

    [Fact]
    public void Selects_the_primary_by_priority()
    {
        var result = Select([Candidate(ProviderRefB, 2), Candidate(ProviderRefA, 1)]).Value;

        Assert.Equal(ProviderRefA, result.SelectedProvider);
        Assert.Equal(1, result.Priority);
        Assert.Equal("PRIMARY_ELIGIBLE_PRIORITY_1", result.Reason);
        Assert.Equal(7, result.RuleVersion);
        Assert.Empty(result.Exclusions);
    }

    public static TheoryData<string, RoutingExclusionReason> ExcludedPrimaries() => new()
    {
        { "route-disabled", RoutingExclusionReason.RouteDisabled },
        { "provider-inactive", RoutingExclusionReason.ProviderInactive },
        { "no-operational-state", RoutingExclusionReason.OperationalStateMissing },
        { "manual-disabled", RoutingExclusionReason.ManualDisabled },
        { "circuit-open", RoutingExclusionReason.CircuitOpen },
        { "unhealthy", RoutingExclusionReason.Unhealthy },
        { "no-capability", RoutingExclusionReason.CapabilityUnsupported },
    };

    [Theory]
    [MemberData(nameof(ExcludedPrimaries))]
    public void Ineligible_primary_falls_back_to_the_secondary(string condition, RoutingExclusionReason reason)
    {
        var primary = condition switch
        {
            "route-disabled" => Candidate(ProviderRefA, 1) with { RouteEnabled = false },
            "provider-inactive" => Candidate(ProviderRefA, 1) with { ProviderActive = false },
            "no-operational-state" => Candidate(ProviderRefA, 1) with { Operational = null },
            "manual-disabled" => Candidate(ProviderRefA, 1) with { Operational = Healthy with { ManualEnabled = false } },
            "circuit-open" => Candidate(ProviderRefA, 1) with { Operational = Healthy with { Circuit = CircuitState.Open } },
            "unhealthy" => Candidate(ProviderRefA, 1) with { Operational = Healthy with { Health = ProviderHealth.Unhealthy } },
            _ => Candidate(ProviderRefA, 1, ProviderCapabilities.Inquiry),
        };

        var result = Select([primary, Candidate(ProviderRefB, 2)]).Value;

        Assert.Equal(ProviderRefB, result.SelectedProvider);
        Assert.Equal("FALLBACK_PRIORITY_2", result.Reason);
        Assert.Equal(new RoutingExclusion(ProviderRefA, 1, reason), Assert.Single(result.Exclusions));
    }

    [Fact]
    public void Degraded_and_half_open_providers_stay_eligible()
    {
        var degraded = Candidate(ProviderRefA, 1) with { Operational = Healthy with { Health = ProviderHealth.Degraded } };
        var halfOpen = Candidate(ProviderRefA, 1) with { Operational = Healthy with { Circuit = CircuitState.HalfOpen } };

        Assert.Equal(ProviderRefA, Select([degraded]).Value.SelectedProvider);
        Assert.Equal(ProviderRefA, Select([halfOpen]).Value.SelectedProvider);
    }

    [Fact]
    public void Expired_temporary_manual_disable_no_longer_excludes()
    {
        var expired = Candidate(ProviderRefA, 1) with { Operational = Healthy with { ManualEnabled = false, ManualDisabledUntil = T0.AddMinutes(-1) } };
        var active = Candidate(ProviderRefA, 1) with { Operational = Healthy with { ManualEnabled = false, ManualDisabledUntil = T0.AddMinutes(1) } };

        Assert.True(Select([expired]).IsSuccess);
        Assert.Equal(ErrorCodes.NoRouteAvailable, Select([active]).Error.Code);
    }

    [Fact]
    public void Excluded_provider_after_a_safe_failure_moves_to_the_next_priority()
    {
        var result = Select([Candidate(ProviderRefA, 1), Candidate(ProviderRefB, 2), Candidate(ProviderC, 3)], excluded: [ProviderA]).Value;

        Assert.Equal(ProviderRefB, result.SelectedProvider);
        Assert.Equal(RoutingExclusionReason.PreviousSafeFailure, Assert.Single(result.Exclusions).Reason);
    }

    [Fact]
    public void No_eligible_provider_is_no_route_available_with_the_reasons()
    {
        var result = Select([
            Candidate(ProviderRefA, 1) with { Operational = Healthy with { Circuit = CircuitState.Open } },
            Candidate(ProviderRefB, 2) with { Operational = Healthy with { ManualEnabled = false } },
        ]);

        Assert.Equal(ErrorCodes.NoRouteAvailable, result.Error.Code);
        Assert.Equal(ErrorCategory.Routing, result.Error.Category);
        Assert.Contains("BANK_A=CircuitOpen", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("BANK_B=ManualDisabled", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal(ErrorCodes.NoRouteAvailable, Select([]).Error.Code);
    }

    [Theory]
    [InlineData(TransactionType.Settlement)]
    [InlineData(TransactionType.TopUp)]
    [InlineData(TransactionType.Adjustment)]
    public void Internal_flows_are_never_routed(TransactionType type)
    {
        Assert.Equal(ErrorCodes.NoRouteAvailable, Select([Candidate(ProviderRefA, 1)], type).Error.Code);
    }

    [Theory]
    [InlineData(TransactionType.Payment, ProviderCapabilities.Payment)]
    [InlineData(TransactionType.Purchase, ProviderCapabilities.Purchase)]
    [InlineData(TransactionType.Inquiry, ProviderCapabilities.Inquiry)]
    [InlineData(TransactionType.BalanceInquiry, ProviderCapabilities.BalanceCheck)]
    [InlineData(TransactionType.Refund, ProviderCapabilities.Refund)]
    [InlineData(TransactionType.Reversal, ProviderCapabilities.Reversal)]
    public void Required_capability_follows_the_transaction_type(TransactionType type, string capability)
    {
        Assert.Equal(capability, ProviderCapabilities.RequiredFor(type));
        Assert.True(Select([Candidate(ProviderRefA, 1, capability)], type).IsSuccess);
    }

    [Theory]
    [InlineData(TransactionType.Transfer, ProviderCapabilities.Transfer)]
    [InlineData(TransactionType.Void, ProviderCapabilities.Void)]
    public void Transfer_and_void_use_their_own_capabilities(TransactionType type, string capability)
    {
        // ADR-017
        Assert.Equal(capability, ProviderCapabilities.RequiredFor(type));
        Assert.True(Select([Candidate(ProviderRefA, 1, capability)], type).IsSuccess);
    }

    [Fact]
    public void Void_is_never_satisfied_by_reversal_or_refund_capabilities()
    {
        var reversalAndRefundOnly = Candidate(ProviderRefA, 1, ProviderCapabilities.Reversal, ProviderCapabilities.Refund);

        var result = Select([reversalAndRefundOnly], TransactionType.Void);

        Assert.Equal(ErrorCodes.NoRouteAvailable, result.Error.Code);
        Assert.Contains("BANK_A=CapabilityUnsupported", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal(ErrorCodes.NoRouteAvailable, Select([Candidate(ProviderRefA, 1, ProviderCapabilities.Void)], TransactionType.Reversal).Error.Code);
        Assert.Equal(ErrorCodes.NoRouteAvailable, Select([Candidate(ProviderRefA, 1, ProviderCapabilities.Void)], TransactionType.Refund).Error.Code);
    }

    [Fact]
    public void Result_becomes_the_initial_routing_decision()
    {
        var decision = Select([Candidate(ProviderRefA, 1)]).Value.ToInitialDecision().Value;

        Assert.Equal((ProviderRefA, ProviderRefA, 7L, 0), (decision.InitialProvider, decision.CurrentProvider, decision.RuleVersion, decision.FailoverCount));
    }

    private static Result<RoutingResult> Select(
        IReadOnlyCollection<ProviderRouteCandidate> candidates, TransactionType type = TransactionType.Payment, ProviderId[]? excluded = null) =>
        RoutingPolicy.Select(
            new RoutingRequest(NewTransactionId(), type, Product, Merchant, Channel, Rp(100_000m), excluded ?? []),
            candidates, ConfigVersion, ruleVersion: 7, T0);

    private static ProviderRouteCandidate Candidate(ProviderReference provider, short priority, params string[] capabilities) =>
        new(provider, priority, RouteEnabled: true, ProviderActive: true, Healthy,
            (capabilities.Length == 0
                ? [ProviderCapabilities.Payment, ProviderCapabilities.StatusCheck]
                : capabilities).ToHashSet());
}
