using Ransys.Domain.Monetary;

namespace Ransys.Domain.Ledger;

/// <summary>
/// Definition of one ledger account (DDL v1.1 <c>ledger.ledger_accounts</c>). Accounts are created lazily and
/// idempotently by code.
/// </summary>
public sealed record LedgerAccountSpec(
    string Code,
    string Name,
    string OwnerType,
    Guid? OwnerId,
    WalletId? WalletId,
    AccountClass AccountClass,
    string AccountType,
    CurrencyDefinition Currency,
    EntrySide NormalSide);

/// <summary>
/// Placeholder chart of accounts (ADR-007; Ledger Posting Rule Matrix §3, §7). Final codes come from Finance;
/// every code is produced here so adopting them is a data migration.
/// The currency definition part of system codes is rendered as <c>&lt;code&gt;-V&lt;version&gt;</c>, which is
/// unique per definition (DDL <c>uq_currency_definition</c>) and keeps the domain independent of database UUIDs
/// (Canonical Data Model §138).
/// </summary>
public static class LedgerAccounts
{
    public const string MerchantOwner = "MERCHANT";
    public const string ProviderOwner = "PROVIDER";
    public const string SystemOwner = "SYSTEM";

    public static LedgerAccountSpec MerchantAvailable(Wallet wallet) => new(
        $"MERCHANT:{wallet.Id}:AVAILABLE", "Merchant available balance", MerchantOwner, wallet.MerchantId.Value, wallet.Id,
        AccountClass.Liability, "MERCHANT_AVAILABLE", wallet.Currency, EntrySide.Credit);

    public static LedgerAccountSpec MerchantReserved(Wallet wallet) => new(
        $"MERCHANT:{wallet.Id}:RESERVED", "Merchant reserved balance", MerchantOwner, wallet.MerchantId.Value, wallet.Id,
        AccountClass.Liability, "MERCHANT_RESERVED", wallet.Currency, EntrySide.Credit);

    public static LedgerAccountSpec ProviderPayable(ProviderId provider, CurrencyDefinition currency) => new(
        $"SYSTEM:PROVIDER_PAYABLE:{provider}:{CurrencyPart(currency)}", "Provider payable", ProviderOwner, provider.Value, null,
        AccountClass.Liability, "PROVIDER_PAYABLE", currency, EntrySide.Credit);

    public static LedgerAccountSpec ProviderReceivable(ProviderId provider, CurrencyDefinition currency) => new(
        $"SYSTEM:PROVIDER_RECEIVABLE:{provider}:{CurrencyPart(currency)}", "Provider receivable", ProviderOwner, provider.Value, null,
        AccountClass.Asset, "PROVIDER_RECEIVABLE", currency, EntrySide.Debit);

    public static LedgerAccountSpec FeeRevenue(CurrencyDefinition currency) => System(
        "RANSYS_FEE_REVENUE", "RANSYS fee revenue", AccountClass.Revenue, EntrySide.Credit, currency);

    public static LedgerAccountSpec TaxPayable(CurrencyDefinition currency) => System(
        "TAX_PAYABLE", "Tax payable", AccountClass.Liability, EntrySide.Credit, currency);

    public static LedgerAccountSpec CashClearing(CurrencyDefinition currency) => System(
        "CASH_CLEARING", "Cash clearing", AccountClass.Asset, EntrySide.Debit, currency);

    /// <summary>Control/suspense account; must not keep unexplained balances (Ledger Posting Rule Matrix §26).</summary>
    public static LedgerAccountSpec AdjustmentClearing(CurrencyDefinition currency) => System(
        "ADJUSTMENT_CLEARING", "Adjustment clearing", AccountClass.Control, EntrySide.Debit, currency);

    private static LedgerAccountSpec System(
        string type, string name, AccountClass accountClass, EntrySide normalSide, CurrencyDefinition currency) =>
        new($"SYSTEM:{type}:{CurrencyPart(currency)}", name, SystemOwner, null, null, accountClass, type, currency, normalSide);

    private static string CurrencyPart(CurrencyDefinition currency) => $"{currency.Code}-V{currency.Version}";
}
