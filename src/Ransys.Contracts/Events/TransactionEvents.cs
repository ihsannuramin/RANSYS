namespace Ransys.Contracts.Events;

/// <summary>Transaction state events (State Transition Matrix §62, Sequence Pack §23).</summary>
public static class TransactionEventTypes
{
    public const string AggregateType = "TRANSACTION";

    public const string Succeeded = "TRANSACTION_SUCCEEDED";
    public const string Failed = "TRANSACTION_FAILED";
    public const string Pending = "TRANSACTION_PENDING";
    public const string InDoubt = "TRANSACTION_IN_DOUBT";
    public const string ResolvedSuccess = "TRANSACTION_RESOLVED_SUCCESS";
    public const string ResolvedFailed = "TRANSACTION_RESOLVED_FAILED";
    public const string ReconExceptionCreated = "RECON_EXCEPTION_CREATED";
    public const string ReversalRequested = "REVERSAL_REQUESTED";
    public const string Reversed = "TRANSACTION_REVERSED";
}

/// <summary>
/// Snapshot of the four status dimensions after a change, for Backoffice replication (Architecture Spec §37:
/// status changes must update the existing Backoffice record). <c>source_version</c> on the envelope is the
/// transaction row version, so consumers can discard out-of-order deliveries.
/// PLACEHOLDER – subject to the Event Contract design.
/// </summary>
public sealed record TransactionStatusChangedV1(
    Guid TransactionId,
    string ClientReference,
    string ProcessingStatus,
    string FinancialStatus,
    string ReconciliationStatus,
    string SettlementStatus,
    string? ResponseCode,
    string? ReasonCode,
    string ChangeSource,
    DateTimeOffset OccurredAt)
{
    public const int Version = 1;
}
