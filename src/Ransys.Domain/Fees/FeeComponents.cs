using System.Collections.Immutable;
using Ransys.Domain.Common;
using Ransys.Domain.Monetary;

namespace Ransys.Domain.Fees;

/// <summary>
/// The fee components of one transaction, all in the transaction currency definition,
/// with the derived totals from Canonical Data Model §16–17.
/// </summary>
public sealed class FeeComponents
{
    private FeeComponents(
        ImmutableArray<FeeComponent> items,
        Money merchantChargeTotal,
        Money refundableFeeTotal)
    {
        Items = items;
        MerchantChargeTotal = merchantChargeTotal;
        RefundableFeeTotal = refundableFeeTotal;
    }

    public ImmutableArray<FeeComponent> Items { get; }

    public CurrencyDefinition Currency => MerchantChargeTotal.Currency;

    /// <summary>Sum of <see cref="FeeComponent.ChargedAmount"/>; persisted as <c>merchant_charge_amount</c>.</summary>
    public Money MerchantChargeTotal { get; }

    /// <summary>
    /// Merchant charges that must be held at reservation (Architecture Spec §10: amount + all guaranteed merchant fees).
    /// Every charged component is fixed when the transaction reserves (Ledger Posting Rule Matrix §49), so this
    /// equals <see cref="MerchantChargeTotal"/>.
    /// TODO / Architecture Decision Required: variable post-response merchant charges would need a
    /// "maximum guaranteed charge" per component (Ledger Posting Rule Matrix §48).
    /// </summary>
    public Money GuaranteedReserveFeeTotal => MerchantChargeTotal;

    /// <summary>Sum of charged amounts on refundable components.</summary>
    public Money RefundableFeeTotal { get; }

    public static FeeComponents None(CurrencyDefinition currency) =>
        new([], Money.Zero(currency), Money.Zero(currency));

    public static Result<FeeComponents> Create(IEnumerable<FeeComponent> components, CurrencyDefinition currency)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(currency);

        var items = components.ToImmutableArray();
        if (items.Any(c => c is null))
        {
            return RansysError.Validation(ErrorCodes.Required, "Fee components must not contain null entries.", "fees");
        }

        if (items.Any(c => c.ChargedAmount.Currency != currency))
        {
            return RansysError.Validation(
                ErrorCodes.FeeCurrencyMismatch, $"All fee components must use the transaction currency {currency}.", "fees");
        }

        var merchantCharge = Money.Sum(items.Select(c => c.ChargedAmount), currency);
        if (merchantCharge.IsFailure)
        {
            return merchantCharge.Error;
        }

        var refundable = Money.Sum(items.Where(c => c.Refundable).Select(c => c.ChargedAmount), currency);
        if (refundable.IsFailure)
        {
            return refundable.Error;
        }

        return new FeeComponents(items, merchantCharge.Value, refundable.Value);
    }

    /// <summary>
    /// Reserve amount = principal + guaranteed merchant charges (Canonical Data Model §17).
    /// The result is persisted at transaction start and never recalculated from current configuration.
    /// </summary>
    public Result<Money> CalculateReserveAmount(Money principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return principal.Currency != Currency
            ? RansysError.Validation(
                ErrorCodes.FeeCurrencyMismatch, "Principal and fees must use the same currency definition.", "amount")
            : principal.Add(GuaranteedReserveFeeTotal);
    }
}
