using Ransys.Application;
using Ransys.Configuration;
using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Fees;
using Ransys.Domain.Monetary;
using Ransys.Domain.Transactions;

namespace Ransys.TransactionCore.Fees;

/// <summary><c>config.fee_rules.fee_type</c>.</summary>
public enum FeeRuleType
{
    Fixed,
    Percentage,
}

/// <summary>
/// One fee rule (<c>config.fee_rules</c>). For <see cref="FeeRuleType.Percentage"/>, <see cref="Value"/> is a decimal
/// rate (0.015 = 1.5 %), ADR-020. <see cref="MerchantId"/> null means a generic rule.
/// </summary>
public sealed record FeeRule(Guid FeeRuleId, MerchantId? MerchantId, FeeRuleType Type, decimal Value, decimal? MinimumFee, decimal? MaximumFee);

/// <summary>Persistence port for <c>config.fee_rules</c>.</summary>
public interface IFeeRuleReader
{
    /// <summary>
    /// Rules of <paramref name="configVersionId"/> for product + transaction type + currency definition, effective at
    /// <paramref name="at"/>, that are generic or specific to <paramref name="merchantId"/>.
    /// </summary>
    Task<IReadOnlyList<FeeRule>> GetRulesAsync(
        IDatabaseSession session,
        Guid configVersionId,
        ProductId productId,
        TransactionType transactionType,
        MerchantId merchantId,
        CurrencyDefinition currency,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);
}

/// <summary>Fees captured on a transaction at validation, with the FEE configuration version they came from.</summary>
public sealed record FeeResolution(FeeComponents Fees, Guid? FeeConfigVersionId, FeeRule? AppliedRule);

/// <summary>
/// Minimal fee resolver (ADR-020). The result is captured on the transaction at validation and never recalculated
/// (Ledger Posting Rule Matrix §48–49):
/// <list type="bullet">
/// <item>rules come from the ACTIVE FEE configuration version; no active version or no matching rule ⇒ zero fee;</item>
/// <item>a merchant-specific rule wins over a generic one; two candidates at the same precedence ⇒ fail closed
/// (<see cref="AmbiguousFeeRule"/>);</item>
/// <item>FIXED ⇒ the value; PERCENTAGE ⇒ amount × rate; then clamped to minimum/maximum and rounded half away from
/// zero to the currency scale;</item>
/// <item>one MERCHANT_SERVICE_FEE component, beneficiary RANSYS, refund policy NONE, rule version = FEE config version.</item>
/// </list>
/// </summary>
public sealed class FeeResolver(ConfigurationService configuration, IFeeRuleReader rules)
{
    public const string AmbiguousFeeRule = "FEE_RULE_AMBIGUOUS";
    public const string Beneficiary = "RANSYS";

    public async Task<Result<FeeResolution>> ResolveAsync(
        IDatabaseSession session,
        ProductId productId,
        TransactionType transactionType,
        MerchantId merchantId,
        Money amount,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(amount);

        var version = await configuration.GetActiveAsync(session, ConfigDomains.Fee, cancellationToken);
        if (version.IsFailure)
        {
            // ADR-020: no active FEE configuration means no fee rules, i.e. zero fee (not a routing-style fail closed:
            // a missing fee can never overdraw or double-charge a merchant).
            return version.Error.Code == ErrorCodes.ConfigurationNotAvailable
                ? new FeeResolution(FeeComponents.None(amount.Currency), null, null)
                : version.Error;
        }

        var candidates = await rules.GetRulesAsync(
            session, version.Value.Id, productId, transactionType, merchantId, amount.Currency, at, cancellationToken);
        var specific = candidates.Where(r => r.MerchantId == merchantId).ToList();
        var generic = candidates.Where(r => r.MerchantId is null).ToList();
        var chosen = specific.Count > 0 ? specific : generic;
        if (chosen.Count > 1)
        {
            return new RansysError(
                AmbiguousFeeRule, ErrorCategory.Internal,
                $"{chosen.Count} fee rules apply to product {productId} {CanonicalCodes.TransactionType.ToCode(transactionType)} at the same precedence.");
        }

        if (chosen.Count == 0)
        {
            return new FeeResolution(FeeComponents.None(amount.Currency), version.Value.Id, null);
        }

        var rule = chosen[0];
        var fee = Calculate(rule, amount);
        if (fee.IsFailure)
        {
            return fee.Error;
        }

        if (fee.Value.IsZero)
        {
            return new FeeResolution(FeeComponents.None(amount.Currency), version.Value.Id, rule);
        }

        var component = FeeComponent.Create(
            FeeComponentType.MerchantServiceFee, fee.Value, fee.Value, FeeBeneficiary.Create(Beneficiary).Value,
            FeeRefundPolicy.None, version.Value.VersionNo);
        if (component.IsFailure)
        {
            return component.Error;
        }

        var components = FeeComponents.Create([component.Value], amount.Currency);
        return components.IsSuccess
            ? new FeeResolution(components.Value, version.Value.Id, rule)
            : components.Error;
    }

    /// <summary>ADR-020 calculation: FIXED or PERCENTAGE × amount, clamp to min/max, round half away from zero to scale.</summary>
    public static Result<Money> Calculate(FeeRule rule, Money amount)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(amount);

        if (rule.Value < 0 || rule.MinimumFee < 0 || rule.MaximumFee < 0 || rule.MaximumFee < rule.MinimumFee)
        {
            return new RansysError(ErrorCodes.OutOfRange, ErrorCategory.Internal, $"Fee rule {rule.FeeRuleId} has invalid values.");
        }

        var raw = rule.Type == FeeRuleType.Fixed ? rule.Value : amount.Amount * rule.Value;
        if (rule.MinimumFee is { } min && raw < min)
        {
            raw = min;
        }

        if (rule.MaximumFee is { } max && raw > max)
        {
            raw = max;
        }

        return Money.Create(decimal.Round(raw, amount.CurrencyScale, MidpointRounding.AwayFromZero), amount.Currency);
    }
}
