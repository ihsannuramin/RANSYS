using System.Collections.Frozen;
using Ransys.Domain.Attempts;
using Ransys.Domain.Fees;
using Ransys.Domain.Transactions;

namespace Ransys.Domain;

/// <summary>
/// Explicit mapping between canonical enums and their SCREAMING_SNAKE codes used in PostgreSQL
/// (VARCHAR + CHECK, ERD v1.1 §3), events and fingerprints. Renaming a C# member must never change
/// a persisted value, so every code is spelled out here.
/// </summary>
public static class CanonicalCodes
{
    public static readonly CodeMap<TransactionType> TransactionType = new(
        (Transactions.TransactionType.Inquiry, "INQUIRY"),
        (Transactions.TransactionType.Payment, "PAYMENT"),
        (Transactions.TransactionType.Purchase, "PURCHASE"),
        (Transactions.TransactionType.Transfer, "TRANSFER"),
        (Transactions.TransactionType.Refund, "REFUND"),
        (Transactions.TransactionType.Reversal, "REVERSAL"),
        (Transactions.TransactionType.Void, "VOID"),
        (Transactions.TransactionType.Advice, "ADVICE"),
        (Transactions.TransactionType.BalanceInquiry, "BALANCE_INQUIRY"),
        (Transactions.TransactionType.StatusCheck, "STATUS_CHECK"),
        (Transactions.TransactionType.Settlement, "SETTLEMENT"),
        (Transactions.TransactionType.TopUp, "TOPUP"),
        (Transactions.TransactionType.Adjustment, "ADJUSTMENT"));

    public static readonly CodeMap<ProcessingStatus> ProcessingStatus = new(
        (Transactions.ProcessingStatus.Received, "RECEIVED"),
        (Transactions.ProcessingStatus.Validated, "VALIDATED"),
        (Transactions.ProcessingStatus.Processing, "PROCESSING"),
        (Transactions.ProcessingStatus.Pending, "PENDING"),
        (Transactions.ProcessingStatus.InDoubt, "IN_DOUBT"),
        (Transactions.ProcessingStatus.Success, "SUCCESS"),
        (Transactions.ProcessingStatus.Failed, "FAILED"),
        (Transactions.ProcessingStatus.ReversalPending, "REVERSAL_PENDING"),
        (Transactions.ProcessingStatus.Reversed, "REVERSED"),
        (Transactions.ProcessingStatus.RefundPending, "REFUND_PENDING"),
        (Transactions.ProcessingStatus.PartiallyRefunded, "PARTIALLY_REFUNDED"),
        (Transactions.ProcessingStatus.Refunded, "REFUNDED"));

    public static readonly CodeMap<FinancialStatus> FinancialStatus = new(
        (Transactions.FinancialStatus.None, "NONE"),
        (Transactions.FinancialStatus.Reserved, "RESERVED"),
        (Transactions.FinancialStatus.Posted, "POSTED"),
        (Transactions.FinancialStatus.Released, "RELEASED"),
        (Transactions.FinancialStatus.ReversalPending, "REVERSAL_PENDING"),
        (Transactions.FinancialStatus.Reversed, "REVERSED"),
        (Transactions.FinancialStatus.RefundPending, "REFUND_PENDING"),
        (Transactions.FinancialStatus.PartiallyRefunded, "PARTIALLY_REFUNDED"),
        (Transactions.FinancialStatus.Refunded, "REFUNDED"),
        (Transactions.FinancialStatus.Adjusted, "ADJUSTED"));

    public static readonly CodeMap<ReconciliationStatus> ReconciliationStatus = new(
        (Transactions.ReconciliationStatus.Unmatched, "UNMATCHED"),
        (Transactions.ReconciliationStatus.Pending, "PENDING"),
        (Transactions.ReconciliationStatus.Matched, "MATCHED"),
        (Transactions.ReconciliationStatus.Exception, "EXCEPTION"),
        (Transactions.ReconciliationStatus.Resolved, "RESOLVED"));

    public static readonly CodeMap<SettlementStatus> SettlementStatus = new(
        (Transactions.SettlementStatus.NotApplicable, "NOT_APPLICABLE"),
        (Transactions.SettlementStatus.Pending, "PENDING"),
        (Transactions.SettlementStatus.Included, "INCLUDED"),
        (Transactions.SettlementStatus.Approved, "APPROVED"),
        (Transactions.SettlementStatus.ReadyToPay, "READY_TO_PAY"),
        (Transactions.SettlementStatus.Settled, "SETTLED"),
        (Transactions.SettlementStatus.Adjusted, "ADJUSTED"));

    public static readonly CodeMap<EndpointType> EndpointType = new(
        (Transactions.EndpointType.Merchant, "MERCHANT"),
        (Transactions.EndpointType.Customer, "CUSTOMER"),
        (Transactions.EndpointType.BankAccount, "BANK_ACCOUNT"),
        (Transactions.EndpointType.Wallet, "WALLET"),
        (Transactions.EndpointType.Biller, "BILLER"),
        (Transactions.EndpointType.Provider, "PROVIDER"),
        (Transactions.EndpointType.VirtualAccount, "VIRTUAL_ACCOUNT"),
        (Transactions.EndpointType.MobileNumber, "MOBILE_NUMBER"),
        (Transactions.EndpointType.Custom, "CUSTOM"));

    public static readonly CodeMap<AttemptType> AttemptType = new(
        (Attempts.AttemptType.Payment, "PAYMENT"),
        (Attempts.AttemptType.Inquiry, "INQUIRY"),
        (Attempts.AttemptType.StatusCheck, "STATUS_CHECK"),
        (Attempts.AttemptType.Reversal, "REVERSAL"),
        (Attempts.AttemptType.Refund, "REFUND"),
        (Attempts.AttemptType.Advice, "ADVICE"));

    public static readonly CodeMap<TransportStatus> TransportStatus = new(
        (Attempts.TransportStatus.NotSent, "NOT_SENT"),
        (Attempts.TransportStatus.Sent, "SENT"),
        (Attempts.TransportStatus.Response, "RESPONSE"),
        (Attempts.TransportStatus.Timeout, "TIMEOUT"),
        (Attempts.TransportStatus.ConnectionError, "CONNECTION_ERROR"));

    /// <summary>State Transition Matrix §59 (<c>transaction_state_history.change_source</c>).</summary>
    public static readonly CodeMap<ChangeSource> ChangeSource = new(
        (Transactions.ChangeSource.Core, "CORE"),
        (Transactions.ChangeSource.SyncProviderResponse, "SYNC_PROVIDER_RESPONSE"),
        (Transactions.ChangeSource.Callback, "CALLBACK"),
        (Transactions.ChangeSource.StatusCheck, "STATUS_CHECK"),
        (Transactions.ChangeSource.Advice, "ADVICE"),
        (Transactions.ChangeSource.Reconciliation, "RECONCILIATION"),
        (Transactions.ChangeSource.ManualAction, "MANUAL_ACTION"),
        (Transactions.ChangeSource.SystemRecovery, "SYSTEM_RECOVERY"));

    /// <summary>ADR-004 <c>transaction_state_history.status_dimension</c>.</summary>
    public static readonly CodeMap<StatusDimension> StatusDimension = new(
        (Transactions.StatusDimension.Processing, "PROCESSING"),
        (Transactions.StatusDimension.Financial, "FINANCIAL"),
        (Transactions.StatusDimension.Reconciliation, "RECONCILIATION"),
        (Transactions.StatusDimension.Settlement, "SETTLEMENT"));

    public static readonly CodeMap<FeeComponentType> FeeComponentType = new(
        (Fees.FeeComponentType.MerchantServiceFee, "MERCHANT_SERVICE_FEE"),
        (Fees.FeeComponentType.ProviderFee, "PROVIDER_FEE"),
        (Fees.FeeComponentType.Commission, "COMMISSION"),
        (Fees.FeeComponentType.Tax, "TAX"),
        (Fees.FeeComponentType.RansysMargin, "RANSYS_MARGIN"),
        (Fees.FeeComponentType.Other, "OTHER"));
}

/// <summary>Bidirectional, exhaustive enum ↔ code map.</summary>
public sealed class CodeMap<TEnum>
    where TEnum : struct, Enum
{
    private readonly FrozenDictionary<TEnum, string> _toCode;
    private readonly FrozenDictionary<string, TEnum> _fromCode;

    public CodeMap(params (TEnum Value, string Code)[] pairs)
    {
        _toCode = pairs.ToFrozenDictionary(p => p.Value, p => p.Code);
        _fromCode = pairs.ToFrozenDictionary(p => p.Code, p => p.Value, StringComparer.Ordinal);

        var missing = Enum.GetValues<TEnum>().Where(v => !_toCode.ContainsKey(v)).ToList();
        if (missing.Count > 0 || _fromCode.Count != pairs.Length)
        {
            throw new InvalidOperationException(
                $"Code map for {typeof(TEnum).Name} must map every value to a unique code. Missing: {string.Join(", ", missing)}.");
        }
    }

    public IReadOnlyCollection<string> Codes => _fromCode.Keys;

    public string ToCode(TEnum value) =>
        _toCode.TryGetValue(value, out var code)
            ? code
            : throw new ArgumentOutOfRangeException(nameof(value), value, $"Undefined {typeof(TEnum).Name}.");

    /// <summary>Codes are exact (case-sensitive), matching the database CHECK constraints.</summary>
    public bool TryParse(string? code, out TEnum value)
    {
        if (code is not null && _fromCode.TryGetValue(code, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    public TEnum Parse(string code) =>
        TryParse(code, out var value)
            ? value
            : throw new FormatException($"Unknown {typeof(TEnum).Name} code '{code}'.");
}
