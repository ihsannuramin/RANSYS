using Ransys.Domain.Common;
using Ransys.Domain.Monetary;

namespace Ransys.Domain.Fees;

/// <summary>Fee component kinds (Canonical Data Model §15).</summary>
public enum FeeComponentType
{
    MerchantServiceFee,
    ProviderFee,
    Commission,
    Tax,
    RansysMargin,
    Other,
}

/// <summary>Who receives a fee component (e.g. RANSYS, PROVIDER, TAX_AUTHORITY).</summary>
public sealed record FeeBeneficiary
{
    /// <summary>DDL v1.1 <c>beneficiary_type varchar(32)</c>.</summary>
    public const int MaxTypeLength = 32;

    private FeeBeneficiary(string type, Guid? id)
    {
        Type = type;
        Id = id;
    }

    public string Type { get; }

    public Guid? Id { get; }

    public static Result<FeeBeneficiary> Create(string? type, Guid? id = null)
    {
        var error = Text.Required(type, "beneficiary.type", MaxTypeLength);
        if (error is not null)
        {
            return error;
        }

        if (id == Guid.Empty)
        {
            return RansysError.Validation(ErrorCodes.InvalidFormat, "Beneficiary id must not be an empty GUID.", "beneficiary.id");
        }

        return new FeeBeneficiary(type!, id);
    }
}

/// <summary>
/// One fee line with separate merchant charge and accounting amounts (Canonical Data Model §15–16,
/// Ledger Posting Rule Matrix §13, §44). <see cref="ChargedAmount"/> is what the merchant pays for this
/// component; <see cref="AccountingAmount"/> is its accounting split. Merchant charge is not assumed to equal
/// RANSYS revenue.
/// </summary>
public sealed record FeeComponent
{
    private FeeComponent(
        FeeComponentType componentType,
        Money chargedAmount,
        Money accountingAmount,
        FeeBeneficiary beneficiary,
        bool refundable,
        long? calculationRuleVersion)
    {
        ComponentType = componentType;
        ChargedAmount = chargedAmount;
        AccountingAmount = accountingAmount;
        Beneficiary = beneficiary;
        Refundable = refundable;
        CalculationRuleVersion = calculationRuleVersion;
    }

    public FeeComponentType ComponentType { get; }

    public Money ChargedAmount { get; }

    public Money AccountingAmount { get; }

    public FeeBeneficiary Beneficiary { get; }

    public bool Refundable { get; }

    /// <summary>Fee rule version captured at reservation; finalization never recalculates (Ledger Matrix §48–49).</summary>
    public long? CalculationRuleVersion { get; }

    public static Result<FeeComponent> Create(
        FeeComponentType componentType,
        Money chargedAmount,
        Money accountingAmount,
        FeeBeneficiary beneficiary,
        bool refundable,
        long? calculationRuleVersion)
    {
        ArgumentNullException.ThrowIfNull(chargedAmount);
        ArgumentNullException.ThrowIfNull(accountingAmount);
        ArgumentNullException.ThrowIfNull(beneficiary);

        if (!Enum.IsDefined(componentType))
        {
            return RansysError.Validation(ErrorCodes.OutOfRange, "Unknown fee component type.", "fee.componentType");
        }

        if (!chargedAmount.HasSameCurrency(accountingAmount))
        {
            return RansysError.Validation(
                ErrorCodes.FeeCurrencyMismatch, "Charged and accounting amounts must use the same currency definition.", "fee");
        }

        if (calculationRuleVersion is <= 0)
        {
            return RansysError.Validation(
                ErrorCodes.OutOfRange, "Calculation rule version must be positive when present.", "fee.calculationRuleVersion");
        }

        return new FeeComponent(componentType, chargedAmount, accountingAmount, beneficiary, refundable, calculationRuleVersion);
    }
}
