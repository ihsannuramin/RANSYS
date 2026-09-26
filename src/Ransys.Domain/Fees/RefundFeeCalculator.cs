using System.Collections.Immutable;
using Ransys.Domain.Common;
using Ransys.Domain.Monetary;

namespace Ransys.Domain.Fees;

/// <summary>Fee returned for one original fee component by one refund.</summary>
public sealed record FeeComponentRefund(FeeComponent Component, Money Amount);

/// <summary>Principal and fee parts of one refund.</summary>
public sealed record RefundAmounts(Money Principal, Money Fee, ImmutableArray<FeeComponentRefund> Components)
{
    public Money Total => Principal.Add(Fee).Value;
}

/// <summary>
/// Computes the fee part of a refund from the original transaction's captured fee components (ADR-014). Current fee
/// configuration is never consulted.
/// <para>
/// Each policy is a deterministic function of the cumulative refunded principal <c>P</c>, so no per-component refund
/// history is needed: the fee of one refund is <c>f(P_before + refund) − f(P_before)</c>.
/// </para>
/// <list type="bullet">
/// <item><c>NONE</c>: f(P) = 0.</item>
/// <item><c>PRO_RATA</c>: f(P) = truncate(charged × P ÷ principal) to the currency scale. This never over-refunds, and
/// f(principal) = charged exactly.</item>
/// <item><c>FULL</c>: f(P) = charged when P = principal, otherwise 0.</item>
/// </list>
/// </summary>
public static class RefundFeeCalculator
{
    public static Result<RefundAmounts> Calculate(
        Money originalPrincipal,
        FeeComponents originalFees,
        Money principalRefundedBefore,
        Money refundPrincipal)
    {
        ArgumentNullException.ThrowIfNull(originalPrincipal);
        ArgumentNullException.ThrowIfNull(originalFees);
        ArgumentNullException.ThrowIfNull(principalRefundedBefore);
        ArgumentNullException.ThrowIfNull(refundPrincipal);

        var currency = originalPrincipal.Currency;
        if (originalFees.Currency != currency || principalRefundedBefore.Currency != currency || refundPrincipal.Currency != currency)
        {
            return RansysError.Validation(ErrorCodes.CurrencyMismatch, "Refund amounts must use the original currency definition.", "amount");
        }

        if (refundPrincipal.IsZero)
        {
            return RansysError.Validation(ErrorCodes.OutOfRange, "Refund principal must be greater than zero.", "amount");
        }

        var after = principalRefundedBefore.Add(refundPrincipal);
        if (after.IsFailure || after.Value.IsGreaterThan(originalPrincipal))
        {
            return RansysError.Financial(
                ErrorCodes.RefundExceedsPosted,
                $"Refunded principal would exceed the original principal {originalPrincipal.ToCanonicalAmountString()}.");
        }

        var components = ImmutableArray.CreateBuilder<FeeComponentRefund>();
        var fee = Money.Zero(currency);
        foreach (var component in originalFees.Items)
        {
            var amount = CumulativeFee(component, originalPrincipal, after.Value.Amount)
                - CumulativeFee(component, originalPrincipal, principalRefundedBefore.Amount);
            var money = Money.Create(amount, currency).Value;
            components.Add(new FeeComponentRefund(component, money));
            fee = fee.Add(money).Value;
        }

        return new RefundAmounts(refundPrincipal, fee, components.ToImmutable());
    }

    private static decimal CumulativeFee(FeeComponent component, Money originalPrincipal, decimal refundedPrincipal)
    {
        var charged = component.ChargedAmount.Amount;
        return component.RefundPolicy switch
        {
            FeeRefundPolicy.ProRata => decimal.Round(
                charged * refundedPrincipal / originalPrincipal.Amount, component.ChargedAmount.CurrencyScale, MidpointRounding.ToZero),
            FeeRefundPolicy.Full => refundedPrincipal == originalPrincipal.Amount ? charged : 0m,
            _ => 0m,
        };
    }
}
