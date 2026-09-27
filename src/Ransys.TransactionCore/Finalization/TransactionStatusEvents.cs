using Ransys.Application;
using Ransys.Contracts.Events;
using Ransys.Domain;
using Ransys.Domain.Transactions;

namespace Ransys.TransactionCore.Finalization;

/// <summary>
/// Builds the <c>TRANSACTION</c> status-changed outbox message (Sequence Pack §23) for a transition that was
/// actually <see cref="TransitionKind.Applied"/> or <see cref="TransitionKind.ConflictRecorded"/>. Shared by
/// <see cref="TransactionFinalizationService"/> and <c>AttemptRecoveryService</c> (R5) so both producers of this
/// event agree on the event type mapping and payload shape.
/// </summary>
internal static class TransactionStatusEvents
{
    /// <summary>
    /// <paramref name="ids"/>/<paramref name="clock"/> mint the outbox envelope; the payload is the transaction's
    /// current four status dimensions. <paramref name="outcome"/>.Kind picks the event type: a contradiction becomes
    /// <see cref="TransactionEventTypes.ReconExceptionCreated"/>, otherwise the processing status (with
    /// <paramref name="previous"/> distinguishing a fresh resolution of IN_DOUBT) picks it. Callers must not invoke
    /// this for a <see cref="TransitionKind.NoChange"/> outcome (nothing happened, so nothing is reported).
    /// <see cref="OutboxMessage.SourceVersion"/> is <paramref name="transaction"/>'s current <c>RowVersion</c>,
    /// which callers must read only after persisting the transaction (the store bumps it in-memory on save).
    /// </summary>
    internal static OutboxMessage Build(
        Transaction transaction, ProcessingStatus previous, TransitionOutcome outcome, ChangeSource source, IIdGenerator ids, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(clock);

        var eventType = outcome.Kind == TransitionKind.ConflictRecorded
            ? TransactionEventTypes.ReconExceptionCreated
            : transaction.ProcessingStatus switch
            {
                ProcessingStatus.Success when previous == ProcessingStatus.InDoubt => TransactionEventTypes.ResolvedSuccess,
                ProcessingStatus.Success => TransactionEventTypes.Succeeded,
                ProcessingStatus.Failed when previous == ProcessingStatus.InDoubt => TransactionEventTypes.ResolvedFailed,
                ProcessingStatus.Failed => TransactionEventTypes.Failed,
                ProcessingStatus.Pending => TransactionEventTypes.Pending,
                ProcessingStatus.Reversed => TransactionEventTypes.Reversed,
                ProcessingStatus.PartiallyRefunded or ProcessingStatus.Refunded => TransactionEventTypes.Refunded,
                _ => TransactionEventTypes.InDoubt,
            };

        var payload = new TransactionStatusChangedV1(
            transaction.Id.Value,
            transaction.Identity.ClientReference,
            CanonicalCodes.ProcessingStatus.ToCode(transaction.ProcessingStatus),
            CanonicalCodes.FinancialStatus.ToCode(transaction.FinancialStatus),
            CanonicalCodes.ReconciliationStatus.ToCode(transaction.ReconciliationStatus),
            CanonicalCodes.SettlementStatus.ToCode(transaction.SettlementStatus),
            transaction.ResponseCode,
            transaction.ReasonCode,
            CanonicalCodes.ChangeSource.ToCode(source),
            transaction.UpdatedAt);

        return new OutboxMessage(
            ids.NewId(), TransactionEventTypes.AggregateType, transaction.Id.Value, eventType, TransactionStatusChangedV1.Version,
            transaction.RowVersion, payload, clock.UtcNow);
    }
}
