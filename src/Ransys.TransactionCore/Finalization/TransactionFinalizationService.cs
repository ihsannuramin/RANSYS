using Ransys.Application;
using Ransys.Contracts.Events;
using Ransys.Domain;
using Ransys.Domain.Attempts;
using Ransys.Domain.Common;
using Ransys.Domain.Transactions;
using Ransys.Ledger;

namespace Ransys.TransactionCore.Finalization;

/// <summary>A provider result for the original request, from any source (sync response, callback, status check, recon…).</summary>
public sealed record ProviderResultCommand(
    TransactionId TransactionId,
    AttemptResolutionKind Resolution,
    ChangeSource Source,
    string ReasonCode,
    string? ResponseCode = null,
    AttemptId? AttemptId = null,
    string? ReasonDescription = null);

public sealed record FinalizationResult(
    TransitionKind Kind,
    LedgerAction LedgerAction,
    ProcessingStatus ProcessingStatus,
    FinancialStatus FinancialStatus,
    ReconciliationStatus ReconciliationStatus);

/// <summary>
/// Single entry point for applying provider results to a transaction (State Transition Matrix §5, §57–58;
/// Ledger Posting Rule Matrix §39–40). Every source goes through the same steps in one database transaction:
/// <list type="number">
/// <item>lock the transaction row (lock order step 1), so concurrent callbacks, status checks, reconciliation
/// and recovery serialize on the same row;</item>
/// <item>apply the domain transition (duplicates are no-ops, contradictions become reconciliation exceptions);</item>
/// <item>execute the ledger action the domain requested (wallet → reservation → posting key);</item>
/// <item>persist state with optimistic <c>row_version</c>, then enqueue the status event in the same transaction.</item>
/// </list>
/// Provider I/O never happens here.
/// </summary>
public sealed class TransactionFinalizationService
{
    private readonly IDatabaseSessionFactory _sessions;
    private readonly ITransactionRepository _transactions;
    private readonly ILedgerPostingService _ledger;
    private readonly IOutboxWriter _outbox;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;

    public TransactionFinalizationService(
        IDatabaseSessionFactory sessions,
        ITransactionRepository transactions,
        ILedgerPostingService ledger,
        IOutboxWriter outbox,
        IClock clock,
        IIdGenerator ids)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
    }

    /// <summary>Applies the result in its own session; commits only on success (fail closed).</summary>
    public async Task<Result<FinalizationResult>> ApplyAsync(ProviderResultCommand command, CancellationToken cancellationToken = default)
    {
        await using var session = await _sessions.BeginAsync(cancellationToken);
        var result = await ApplyAsync(session, command, cancellationToken);
        if (result.IsSuccess)
        {
            await session.CommitAsync(cancellationToken);
        }

        return result;
    }

    /// <summary>Applies the result inside the caller's session (the caller commits).</summary>
    public async Task<Result<FinalizationResult>> ApplyAsync(
        IDatabaseSession session, ProviderResultCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var context = TransitionContext.Create(
            command.ReasonCode, command.Source, _clock.UtcNow, command.ReasonDescription, command.AttemptId, command.ResponseCode);
        if (context.IsFailure)
        {
            return context.Error;
        }

        var loaded = await _transactions.GetAsync(session, command.TransactionId, forUpdate: true, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        if (loaded.Value is not { } transaction)
        {
            return RansysError.Validation(ErrorCodes.InvalidStateTransition, $"Transaction {command.TransactionId} does not exist.");
        }

        var previous = transaction.ProcessingStatus;
        var transition = command.Resolution switch
        {
            AttemptResolutionKind.Success => transaction.CompleteSuccess(context.Value),
            AttemptResolutionKind.Failed => transaction.CompleteFailure(context.Value),
            AttemptResolutionKind.Pending => transaction.MarkPending(context.Value),
            AttemptResolutionKind.InDoubt => transaction.MarkInDoubt(context.Value),

            // "Not sent" is not a result of the transaction: the caller fails over or fails it before send.
            _ => RansysError.Validation(ErrorCodes.InvalidStateTransition, "A not-sent attempt is handled by failover, not finalization."),
        };
        if (transition.IsFailure)
        {
            return transition.Error;
        }

        var outcome = transition.Value;
        if (outcome.Kind == TransitionKind.NoChange)
        {
            return Snapshot(outcome, transaction);
        }

        var ledger = await ExecuteLedgerActionAsync(session, transaction, outcome.LedgerAction, cancellationToken);
        if (ledger.IsFailure)
        {
            return ledger.Error;
        }

        var saved = await _transactions.UpdateAsync(session, transaction, cancellationToken);
        if (saved.IsFailure)
        {
            return saved.Error;
        }

        await EnqueueStatusEventAsync(session, transaction, previous, outcome, command.Source, cancellationToken);
        return Snapshot(outcome, transaction);
    }

    private async Task<Result> ExecuteLedgerActionAsync(
        IDatabaseSession session, Transaction transaction, LedgerAction action, CancellationToken cancellationToken)
    {
        if (action != LedgerAction.None)
        {
            var posting = action switch
            {
                LedgerAction.Post when transaction.Routing is { } routing =>
                    await _ledger.PostPaymentAsync(
                        session, new PostPaymentRequest(transaction.Id, routing.CurrentProvider.ProviderId), cancellationToken),
                LedgerAction.Release =>
                    await _ledger.ReleaseReservationAsync(session, new ReleaseRequest(transaction.Id), cancellationToken),
                _ => RansysError.Validation(
                    ErrorCodes.InvalidStateTransition, $"Ledger action {action} is not produced by a provider result for the original request."),
            };

            if (posting.IsFailure)
            {
                return posting.Error;
            }
        }

        // OP-05: IN_DOUBT keeps the hold; only its reason changes.
        if (transaction.ProcessingStatus == ProcessingStatus.InDoubt && transaction.FinancialStatus == FinancialStatus.Reserved)
        {
            return await _ledger.ChangeHoldReasonAsync(session, transaction.Id, "IN_DOUBT", cancellationToken);
        }

        return Result.Success();
    }

    private Task EnqueueStatusEventAsync(
        IDatabaseSession session, Transaction transaction, ProcessingStatus previous, TransitionOutcome outcome, ChangeSource source, CancellationToken cancellationToken)
    {
        var eventType = outcome.Kind == TransitionKind.ConflictRecorded
            ? TransactionEventTypes.ReconExceptionCreated
            : transaction.ProcessingStatus switch
            {
                ProcessingStatus.Success when previous == ProcessingStatus.InDoubt => TransactionEventTypes.ResolvedSuccess,
                ProcessingStatus.Success => TransactionEventTypes.Succeeded,
                ProcessingStatus.Failed when previous == ProcessingStatus.InDoubt => TransactionEventTypes.ResolvedFailed,
                ProcessingStatus.Failed => TransactionEventTypes.Failed,
                ProcessingStatus.Pending => TransactionEventTypes.Pending,
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

        return _outbox.EnqueueAsync(
            session,
            new OutboxMessage(
                _ids.NewId(), TransactionEventTypes.AggregateType, transaction.Id.Value, eventType, TransactionStatusChangedV1.Version,
                transaction.RowVersion, payload, _clock.UtcNow),
            cancellationToken);
    }

    private static FinalizationResult Snapshot(TransitionOutcome outcome, Transaction transaction) =>
        new(outcome.Kind, outcome.LedgerAction, transaction.ProcessingStatus, transaction.FinancialStatus, transaction.ReconciliationStatus);
}
