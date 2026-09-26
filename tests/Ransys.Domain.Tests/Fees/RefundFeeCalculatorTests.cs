using Ransys.Domain.Common;
using Ransys.Domain.Fees;
using Ransys.Domain.Monetary;
using static Ransys.Domain.Tests.TestData;

namespace Ransys.Domain.Tests.Fees;

/// <summary>ADR-014 refund fee rules (FULL on completion, PRO_RATA cumulative truncated, NONE default).</summary>
public sealed class RefundFeeCalculatorTests
{
    private static readonly FeeBeneficiary Ransys = FeeBeneficiary.Create("RANSYS").Value;

    [Fact]
    public void None_policy_never_refunds_fee()
    {
        var fees = Fees(Fee(2_500m, FeeRefundPolicy.None));

        var full = Refund(fees, before: 0m, refund: 100_000m);

        Assert.True(full.Fee.IsZero);
        Assert.Equal(Rp(100_000m), full.Total);
    }

    [Fact]
    public void Full_policy_refunds_only_with_the_refund_that_completes_the_principal()
    {
        var fees = Fees(Fee(2_500m, FeeRefundPolicy.Full));

        Assert.True(Refund(fees, before: 0m, refund: 40_000m).Fee.IsZero);
        Assert.True(Refund(fees, before: 40_000m, refund: 59_999.99m).Fee.IsZero);
        Assert.Equal(Rp(2_500m), Refund(fees, before: 99_999.99m, refund: 0.01m).Fee);
        Assert.Equal(Rp(2_500m), Refund(fees, before: 0m, refund: 100_000m).Fee);
    }

    [Fact]
    public void Pro_rata_is_cumulative_truncated_and_sums_exactly_to_the_charged_fee()
    {
        // 1,000.00 fee on 300,000.00 principal refunded in three equal parts: 333.33 + 333.33 + 333.34.
        var fees = Fees(Fee(1_000m, FeeRefundPolicy.ProRata));
        var principal = Rp(300_000m);

        var first = RefundFeeCalculator.Calculate(principal, fees, Rp(0m), Rp(100_000m)).Value;
        var second = RefundFeeCalculator.Calculate(principal, fees, Rp(100_000m), Rp(100_000m)).Value;
        var third = RefundFeeCalculator.Calculate(principal, fees, Rp(200_000m), Rp(100_000m)).Value;

        Assert.Equal([Rp(333.33m), Rp(333.33m), Rp(333.34m)], [first.Fee, second.Fee, third.Fee]);
        Assert.Equal(Rp(1_000m), first.Fee.Add(second.Fee).Value.Add(third.Fee).Value);
    }

    [Fact]
    public void Pro_rata_never_over_refunds_on_any_split()
    {
        var fees = Fees(Fee(999.99m, FeeRefundPolicy.ProRata));
        var principal = Rp(77_777.77m);
        decimal[] parts = [0.01m, 12_345.67m, 1m, 33_333.33m, 32_097.76m];
        Assert.Equal(principal.Amount, parts.Sum());

        var refundedFee = Money.Zero(Idr);
        var refundedPrincipal = Money.Zero(Idr);
        foreach (var part in parts)
        {
            var refund = RefundFeeCalculator.Calculate(principal, fees, refundedPrincipal, Rp(part)).Value;
            refundedFee = refundedFee.Add(refund.Fee).Value;
            refundedPrincipal = refundedPrincipal.Add(refund.Principal).Value;
            Assert.True(refundedFee.Amount <= 999.99m * refundedPrincipal.Amount / principal.Amount);
        }

        Assert.Equal(Rp(999.99m), refundedFee);
    }

    [Fact]
    public void Components_are_computed_independently()
    {
        var fees = Fees(
            Fee(2_000m, FeeRefundPolicy.ProRata),
            Fee(500m, FeeRefundPolicy.Full),
            Fee(300m, FeeRefundPolicy.None));

        var half = Refund(fees, before: 0m, refund: 50_000m);
        var rest = Refund(fees, before: 50_000m, refund: 50_000m);

        Assert.Equal([Rp(1_000m), Rp(0m), Rp(0m)], half.Components.Select(c => c.Amount));
        Assert.Equal([Rp(1_000m), Rp(500m), Rp(0m)], rest.Components.Select(c => c.Amount));
        Assert.Equal(Rp(1_500m), rest.Fee);
    }

    [Fact]
    public void Refund_beyond_the_original_principal_is_rejected()
    {
        var fees = Fees(Fee(2_500m, FeeRefundPolicy.ProRata));

        var result = RefundFeeCalculator.Calculate(Rp(100_000m), fees, Rp(60_000m), Rp(40_000.01m));

        Assert.Equal(ErrorCodes.RefundExceedsPosted, result.Error.Code);
        Assert.Equal(ErrorCodes.OutOfRange, RefundFeeCalculator.Calculate(Rp(100_000m), fees, Rp(0m), Rp(0m)).Error.Code);
    }

    [Fact]
    public void Currency_definition_must_match_the_original()
    {
        var fees = Fees(Fee(2_500m, FeeRefundPolicy.ProRata));

        var result = RefundFeeCalculator.Calculate(Rp(100_000m), fees, Rp(0m), Money.Create(10m, IdrV2).Value);

        Assert.Equal(ErrorCodes.CurrencyMismatch, result.Error.Code);
    }

    [Fact]
    public void Refundable_total_counts_components_with_a_refund_policy()
    {
        var fees = Fees(Fee(2_000m, FeeRefundPolicy.ProRata), Fee(500m, FeeRefundPolicy.Full), Fee(300m, FeeRefundPolicy.None));

        Assert.Equal(Rp(2_500m), fees.RefundableFeeTotal);
        Assert.False(fees.Items[2].Refundable);
    }

    private static RefundAmounts Refund(FeeComponents fees, decimal before, decimal refund) =>
        RefundFeeCalculator.Calculate(Rp(100_000m), fees, Rp(before), Rp(refund)).Value;

    private static FeeComponents Fees(params FeeComponent[] components) => FeeComponents.Create(components, Idr).Value;

    private static FeeComponent Fee(decimal charged, FeeRefundPolicy policy) =>
        FeeComponent.Create(FeeComponentType.MerchantServiceFee, Rp(charged), Rp(charged), Ransys, policy, 1).Value;
}
