using Ransys.Domain.Common;
using Ransys.Domain.Fees;
using Ransys.Domain.Monetary;
using static Ransys.Domain.Tests.TestData;

namespace Ransys.Domain.Tests.Fees;

public sealed class FeeComponentsTests
{
    private static readonly FeeBeneficiary Ransys = FeeBeneficiary.Create("RANSYS").Value;

    [Fact]
    public void Reserve_amount_is_principal_plus_merchant_charges()
    {
        // Architecture Spec §10: amount 100,000 + fee 2,500 => reserve 102,500.
        var fees = FeeComponents.Create([Fee(FeeComponentType.MerchantServiceFee, charged: 2_500m, accounting: 2_500m)], Idr).Value;

        Assert.Equal(Rp(102_500m), fees.CalculateReserveAmount(Rp(100_000m)).Value);
    }

    [Fact]
    public void Merchant_charge_is_not_assumed_to_equal_revenue()
    {
        // Ledger Posting Rule Matrix §13: merchant pays 2,500; accounting split differs.
        var fees = FeeComponents.Create(
        [
            Fee(FeeComponentType.MerchantServiceFee, charged: 2_500m, accounting: 2_750m),
            Fee(FeeComponentType.ProviderFee, charged: 0m, accounting: 500m),
            Fee(FeeComponentType.Tax, charged: 0m, accounting: 250m),
        ], Idr).Value;

        Assert.Equal(Rp(2_500m), fees.MerchantChargeTotal);
        Assert.Equal(Rp(2_500m), fees.GuaranteedReserveFeeTotal);
        Assert.Equal(Rp(102_500m), fees.CalculateReserveAmount(Rp(100_000m)).Value);
    }

    [Fact]
    public void Refundable_total_only_counts_refundable_components()
    {
        var fees = FeeComponents.Create(
        [
            Fee(FeeComponentType.MerchantServiceFee, 2_500m, 2_500m, refundable: false),
            Fee(FeeComponentType.Other, 1_000m, 1_000m, refundable: true),
        ], Idr).Value;

        Assert.Equal(Rp(3_500m), fees.MerchantChargeTotal);
        Assert.Equal(Rp(1_000m), fees.RefundableFeeTotal);
    }

    [Fact]
    public void No_fees_reserves_principal_only()
    {
        Assert.Equal(Rp(50_000m), FeeComponents.None(Idr).CalculateReserveAmount(Rp(50_000m)).Value);
    }

    [Fact]
    public void Fees_must_use_transaction_currency()
    {
        var usdFee = FeeComponent.Create(
            FeeComponentType.Other, Money.Create(1m, Usd).Value, Money.Create(1m, Usd).Value, Ransys, false, null).Value;

        Assert.Equal(ErrorCodes.FeeCurrencyMismatch, FeeComponents.Create([usdFee], Idr).Error.Code);
        Assert.Equal(
            ErrorCodes.FeeCurrencyMismatch,
            FeeComponents.None(Idr).CalculateReserveAmount(Money.Create(1m, Usd).Value).Error.Code);
    }

    [Fact]
    public void Component_amounts_must_share_currency_and_rule_version_must_be_positive()
    {
        Assert.Equal(
            ErrorCodes.FeeCurrencyMismatch,
            FeeComponent.Create(FeeComponentType.Tax, Rp(1m), Money.Create(1m, Usd).Value, Ransys, false, null).Error.Code);
        Assert.Equal(
            ErrorCodes.OutOfRange,
            FeeComponent.Create(FeeComponentType.Tax, Rp(1m), Rp(1m), Ransys, false, 0).Error.Code);
    }

    [Fact]
    public void Beneficiary_type_is_required_and_bounded()
    {
        Assert.Equal(ErrorCodes.Required, FeeBeneficiary.Create(" ").Error.Code);
        Assert.Equal(ErrorCodes.TooLong, FeeBeneficiary.Create(new string('X', 33)).Error.Code);
        Assert.Equal(ErrorCodes.InvalidFormat, FeeBeneficiary.Create("PROVIDER", Guid.Empty).Error.Code);
    }

    private static FeeComponent Fee(FeeComponentType type, decimal charged, decimal accounting, bool refundable = false) =>
        FeeComponent.Create(type, Rp(charged), Rp(accounting), Ransys, refundable, calculationRuleVersion: 7).Value;
}
