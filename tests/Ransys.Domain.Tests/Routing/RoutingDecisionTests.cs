using Ransys.Domain.Common;
using Ransys.Domain.Routing;
using static Ransys.Domain.Tests.TestData;

namespace Ransys.Domain.Tests.Routing;

public sealed class RoutingDecisionTests
{
    [Fact]
    public void Initial_decision_has_same_initial_and_current_provider()
    {
        var decision = RoutingDecision.Initial(ProviderRefA, ruleVersion: 3, T0).Value;

        Assert.Equal(ProviderRefA, decision.InitialProvider);
        Assert.Equal(ProviderRefA, decision.CurrentProvider);
        Assert.Equal(0, decision.FailoverCount);
        Assert.Null(decision.FailoverReason);
    }

    [Fact]
    public void Failover_keeps_initial_provider_and_records_reason_and_count()
    {
        var decision = RoutingDecision.Initial(ProviderRefA, 3, T0).Value
            .FailoverTo(ProviderRefB, "PROVIDER_LINK_DOWN", T0.AddSeconds(1)).Value;

        Assert.Equal(ProviderRefA, decision.InitialProvider);
        Assert.Equal(ProviderRefB, decision.CurrentProvider);
        Assert.Equal(1, decision.FailoverCount);
        Assert.Equal("PROVIDER_LINK_DOWN", decision.FailoverReason);
        Assert.Equal(3, decision.RuleVersion);
    }

    [Fact]
    public void Failover_to_the_current_provider_is_rejected()
    {
        var result = RoutingDecision.Initial(ProviderRefA, 3, T0).Value.FailoverTo(ProviderRefA, "RETRY", T0);

        Assert.Equal(ErrorCodes.RoutingFailoverToSameProvider, result.Error.Code);
    }

    [Fact]
    public void Failover_requires_reason_and_non_decreasing_time()
    {
        var initial = RoutingDecision.Initial(ProviderRefA, 3, T0).Value;

        Assert.Equal(ErrorCodes.RoutingInvalidDecision, initial.FailoverTo(ProviderRefB, " ", T0).Error.Code);
        Assert.Equal(ErrorCodes.RoutingInvalidDecision, initial.FailoverTo(ProviderRefB, "DOWN", T0.AddSeconds(-1)).Error.Code);
    }

    [Theory]
    [InlineData(0, 0, null)]    // rule version must be positive
    [InlineData(3, -1, null)]   // negative failover count
    [InlineData(3, 1, null)]    // failover without reason
    [InlineData(3, 0, "DOWN")]  // reason without failover
    public void Inconsistent_rehydrated_decisions_are_rejected(long ruleVersion, int failoverCount, string? reason)
    {
        var current = failoverCount > 0 ? ProviderRefB : ProviderRefA;

        var result = RoutingDecision.Create(ProviderRefA, current, ruleVersion, failoverCount, reason, T0);

        Assert.Equal(ErrorCodes.RoutingInvalidDecision, result.Error.Code);
    }

    [Fact]
    public void Different_current_provider_without_failover_is_rejected()
    {
        Assert.Equal(
            ErrorCodes.RoutingInvalidDecision,
            RoutingDecision.Create(ProviderRefA, ProviderRefB, 3, 0, null, T0).Error.Code);
    }

    [Fact]
    public void Provider_reference_validates_code_and_adapter()
    {
        Assert.Equal(ErrorCodes.Required, ProviderReference.Create(ProviderA, "", "svc").Error.Code);
        Assert.Equal(ErrorCodes.TooLong, ProviderReference.Create(ProviderA, new string('P', 65), "svc").Error.Code);
        Assert.Equal(ErrorCodes.Required, ProviderReference.Create(ProviderA, "BANK_A", null).Error.Code);
    }
}
