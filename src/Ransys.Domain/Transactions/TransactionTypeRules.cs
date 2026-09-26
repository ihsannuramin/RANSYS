namespace Ransys.Domain.Transactions;

/// <summary>
/// Which transaction families use which financial path in Phase 1.
/// </summary>
public static class TransactionTypeRules
{
    /// <summary>
    /// Merchant-debiting families that must hold a prefund reservation before any provider call
    /// (Architecture Spec §11–12, State Transition Matrix PS-04).
    /// TODO / Architecture Decision Required: VOID, ADVICE and SETTLEMENT financial semantics are not
    /// specified; they are treated as non-reserving until their flows are designed.
    /// </summary>
    public static bool RequiresReservation(TransactionType type) =>
        type is TransactionType.Payment or TransactionType.Purchase or TransactionType.Transfer;

    /// <summary>Child actions that must reference their original transaction (State Transition Matrix §19, ERD v1.1 §51).</summary>
    public static bool RequiresOriginalTransaction(TransactionType type) =>
        type is TransactionType.Refund or TransactionType.Reversal;
}

/// <summary>
/// Configuration versions captured on the transaction; recovery uses these, never the newest config
/// (State Transition Matrix §68, Ledger Posting Rule Matrix §48–49).
/// </summary>
public sealed record TransactionConfigurationSnapshot(
    Guid? ConfigVersionId,
    Guid? RoutingConfigVersionId,
    Guid? FeeConfigVersionId,
    long? ProviderPolicyVersion)
{
    public static TransactionConfigurationSnapshot None { get; } = new(null, null, null, null);
}

/// <summary>Well-known reason codes used by the aggregate itself (State Transition Matrix §61).</summary>
public static class ReasonCodes
{
    public const string TransactionReceived = "TRANSACTION_RECEIVED";
    public const string ConflictingProviderResult = "CONFLICTING_PROVIDER_RESULT";
    public const string ReversalDeclined = "REVERSAL_DECLINED";

    /// <summary>Attempt left without a recorded outcome (e.g. Core restart mid-call); ADR-005 recovery.</summary>
    public const string AttemptOutcomeUnknown = "ATTEMPT_OUTCOME_UNKNOWN";
}
