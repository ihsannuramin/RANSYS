namespace Ransys.Contracts.Events;

/// <summary>Outbox event types emitted by the Ledger Posting Service (Ledger Posting Rule Matrix §54).</summary>
public static class LedgerEventTypes
{
    public const string AggregateType = "WALLET";

    public const string WalletReserved = "WALLET_RESERVED";
    public const string TransactionPosted = "TRANSACTION_POSTED";
    public const string ReservationReleased = "RESERVATION_RELEASED";
    public const string ReversalPosted = "REVERSAL_POSTED";
    public const string RefundPosted = "REFUND_POSTED";
    public const string TopUpPosted = "TOPUP_POSTED";
    public const string AdjustmentPosted = "ADJUSTMENT_POSTED";
    public const string WalletStatusChanged = "WALLET_STATUS_CHANGED";
}

/// <summary>
/// Payload for every ledger posting event, carrying the wallet projection after the posting so Backoffice can
/// show balance mutation history (Ledger Posting Rule Matrix §41).
/// PLACEHOLDER – field set and naming are subject to the Event Contract design (Sequence Pack §23).
/// Amounts are exact decimals in the currency definition's scale.
/// </summary>
public sealed record LedgerPostingEventV1(
    string PostingKey,
    Guid LedgerTransactionId,
    string OperationType,
    Guid? TransactionId,
    Guid WalletId,
    decimal Amount,
    string CurrencyCode,
    int CurrencyDefinitionVersion,
    decimal AvailableBalanceAfter,
    decimal ReservedBalanceAfter,
    decimal LedgerBalanceAfter,
    DateTimeOffset OccurredAt)
{
    public const int Version = 1;
}

/// <summary>
/// Wallet status change (ADR-016), including who changed it and why, for the Backoffice audit trail.
/// PLACEHOLDER – subject to the Event Contract design.
/// </summary>
public sealed record WalletStatusChangedV1(
    Guid WalletId,
    Guid MerchantId,
    string PreviousStatus,
    string NewStatus,
    string Reason,
    string ActorType,
    Guid? ActorId,
    Guid? ApprovalRequestId,
    DateTimeOffset OccurredAt)
{
    public const int Version = 1;
}
