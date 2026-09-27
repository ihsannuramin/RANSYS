using Ransys.Application;
using Ransys.Contracts.Events;
using Ransys.Domain;
using Ransys.Domain.Attempts;
using Ransys.Domain.Common;
using Ransys.Domain.Fees;
using Ransys.Domain.Monetary;
using Ransys.Domain.Routing;
using Ransys.Domain.Transactions;
using Ransys.Ledger;
using Ransys.TransactionCore.Attempts;

namespace Ransys.TransactionCore.Finalization;

/// <summary>
/// A provider result for the original request, from any source (sync response, callback, status check, recon…).
/// <paramref name="ExpectedProvider"/> and <paramref name="Evidence"/> are populated only by a provider callback
/// (ADR-027): they let finalization correlate to the attempt the provider actually answered and persist its
/// evidence under the same lock order as everything else, instead of the caller doing so beforehand.
/// </summary>
public sealed record ProviderResultCommand(
    TransactionId TransactionId,
    AttemptResolutionKind Resolution,
    ChangeSource Source,
    string ReasonCode,
    string? ResponseCode = null,
    AttemptId? AttemptId = null,
    string? ReasonDescription = null,
    ProviderId? ExpectedProvider = null,
    AttemptOutcome? Evidence = null);

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
    private readonly ITransactionAttemptStore _attemptStore;
    private readonly TransactionAttemptService _attemptService;

    public TransactionFinalizationService(
        IDatabaseSessionFactory sessions,
        ITransactionRepository transactions,
        ILedgerPostingService ledger,
        IOutboxWriter outbox,
        IClock clock,
        IIdGenerator ids,
        ITransactionAttemptStore attemptStore,
        TransactionAttemptService attemptService)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _attemptStore = attemptStore ?? throw new ArgumentNullException(nameof(attemptStore));
        _attemptService = attemptService ?? throw new ArgumentNullException(nameof(attemptService));
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

        // Lock order: a child's original (parent) is locked before the child itself (ADR-012, ADR-023, ADR-019).
        var peek = await _transactions.GetAsync(session, command.TransactionId, forUpdate: false, cancellationToken);
        if (peek.IsFailure)
        {
            return peek.Error;
        }

        if (peek.Value is null)
        {
            return RansysError.Validation(ErrorCodes.TransactionNotFound, $"Transaction {command.TransactionId} does not exist.");
        }

        Transaction? original = null;
        if (peek.Value is { Type: TransactionType.Reversal or TransactionType.Refund or TransactionType.Void, Identity.OriginalTransactionId: { } originalId })
        {
            var parent = await _transactions.GetAsync(session, originalId, forUpdate: true, cancellationToken);
            if (parent.IsFailure)
            {
                return parent.Error;
            }

            original = parent.Value
                ?? throw new InvalidOperationException($"Child {command.TransactionId} references missing original {originalId}.");
        }

        var loaded = await _transactions.GetAsync(session, command.TransactionId, forUpdate: true, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var transaction = loaded.Value!;

        // A callback identifies the reporting provider, not the attempt (ADR-027): correlate to the attempt of the
        // current routed provider under the lock just taken, so this never races the sink's own separate read.
        AttemptId? effectiveAttemptId = command.AttemptId;
        if (command.ExpectedProvider is { } expected)
        {
            if (transaction.Routing?.CurrentProvider.ProviderId != expected)
            {
                return RansysError.Validation(
                    ErrorCodes.ProviderMismatch, $"Provider {expected} is not the current provider for transaction {transaction.Id}.");
            }

            var attemptsResult = await _attemptStore.GetByTransactionAsync(session, transaction.Id, cancellationToken);
            if (attemptsResult.IsFailure)
            {
                return attemptsResult.Error;
            }

            var matched = attemptsResult.Value
                .Where(a => a.Provider.ProviderId == expected)
                .OrderByDescending(a => a.AttemptNumber)
                .FirstOrDefault();
            if (matched is null)
            {
                return RansysError.Validation(
                    ErrorCodes.ProviderMismatch, $"No attempt on transaction {transaction.Id} targets provider {expected}.");
            }

            effectiveAttemptId = matched.Id;
            if (!matched.IsOutcomeRecorded && command.Evidence is not null)
            {
                var recorded = await _attemptService.RecordOutcomeAsync(session, matched, command.Evidence, cancellationToken);
                if (recorded.IsFailure)
                {
                    return recorded.Error;
                }
            }
        }

        var context = TransitionContext.Create(
            command.ReasonCode, command.Source, _clock.UtcNow, command.ReasonDescription, effectiveAttemptId, command.ResponseCode);
        if (context.IsFailure)
        {
            return context.Error;
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
            // U1 (ADR-027 revised): a duplicate/consistent report of the already-accepted resolution may still carry
            // evidence that enriches what's stored so far (same identity, fills blanks, non-empty Data wins over
            // empty) — but a genuinely different provider reference/STAN/RRN is never silently swapped in, and a
            // report with no identity at all changes nothing. Only write when the merge actually changed something.
            if (command.Evidence is not null)
            {
                var merge = transaction.MergeLatestProviderResult(command.Evidence, command.Source, _clock.UtcNow);
                switch (merge)
                {
                    case ProviderResultMergeOutcome.Recorded or ProviderResultMergeOutcome.Enriched:
                        var savedEvidence = await _transactions.UpdateAsync(session, transaction, cancellationToken);
                        if (savedEvidence.IsFailure)
                        {
                            return savedEvidence.Error;
                        }

                        break;

                    case ProviderResultMergeOutcome.ConflictingIdentity:
                        // V2 (ADR-027 revised): never silently swallow a conflicting reference/STAN/RRN as an
                        // ordinary duplicate. Record it through the same reconciliation-exception mechanism a
                        // status-level conflict already uses (idempotent when already EXCEPTION), persist it, and
                        // enqueue its status event so the conflict is durable and observable, not just returned
                        // from this call and discarded.
                        var conflict = transaction.RecordProviderEvidenceConflict(context.Value);
                        if (conflict.IsFailure)
                        {
                            return conflict.Error;
                        }

                        var savedConflict = await _transactions.UpdateAsync(session, transaction, cancellationToken);
                        if (savedConflict.IsFailure)
                        {
                            return savedConflict.Error;
                        }

                        if (conflict.Value.Changes.Count > 0)
                        {
                            await EnqueueStatusEventAsync(session, transaction, previous, conflict.Value, command.Source, cancellationToken);
                        }

                        return Snapshot(conflict.Value, transaction);

                    case ProviderResultMergeOutcome.IgnoredNoIdentity:
                        break;
                }
            }

            return Snapshot(outcome, transaction);
        }

        var ledger = await ExecuteLedgerActionAsync(session, transaction, outcome.LedgerAction, cancellationToken);
        if (ledger.IsFailure)
        {
            return ledger.Error;
        }

        // ADR-027 (RR3): the transaction the callback directly targets (the child itself, for a child callback) gets
        // the latest-evidence projection; the parent (if any) is updated by its own helper below when it applies. A
        // conflicting report (TransitionKind.ConflictRecorded) never overwrites the projection: it contradicts the
        // already-accepted resolution, so its evidence is not authoritative — only a genuinely accepted transition
        // (TransitionKind.Applied) may update what readers treat as the transaction's business result. Applied always
        // *replaces* the projection outright (never merges it against a now-superseded prior status's evidence,
        // e.g. a temporary PENDING reference genuinely differing from the real final one) — see
        // Transaction.RecordLatestProviderResult's doc for why this must not go through the NoChange-only merge/U1
        // conflict check.
        if (outcome.Kind == TransitionKind.Applied && command.Evidence is not null)
        {
            transaction.RecordLatestProviderResult(command.Evidence, command.Source, _clock.UtcNow);
        }

        var saved = await _transactions.UpdateAsync(session, transaction, cancellationToken);
        if (saved.IsFailure)
        {
            return saved.Error;
        }

        await EnqueueStatusEventAsync(session, transaction, previous, outcome, command.Source, cancellationToken);

        if (original is not null && outcome.Kind == TransitionKind.Applied && transaction.ProcessingStatus == ProcessingStatus.Success)
        {
            var applied = transaction.Type switch
            {
                TransactionType.Reversal => await ApplyReversalToOriginalAsync(session, original, transaction, command, context.Value, cancellationToken),
                TransactionType.Refund => await ApplyRefundToOriginalAsync(session, original, transaction, command, context.Value, cancellationToken),
                _ => await ApplyVoidToOriginalAsync(session, original, transaction, command, context.Value, cancellationToken),
            };
            if (applied.IsFailure)
            {
                return applied.Error;
            }
        }

        return Snapshot(outcome, transaction);
    }

    /// <summary>
    /// ADR-012: the reversal child succeeded; the original becomes REVERSED and the ledger effect follows the original's
    /// financial state now (release of an active reservation, or compensating reversal of a posted payment).
    /// Same database transaction as the child's success.
    /// </summary>
    private async Task<Result> ApplyReversalToOriginalAsync(
        IDatabaseSession session, Transaction original, Transaction reversal, ProviderResultCommand command, TransitionContext childContext, CancellationToken cancellationToken)
    {
        var context = TransitionContext.Create(
            "REVERSAL_CONFIRMED", command.Source, childContext.OccurredAt, $"Reversal {reversal.Id} confirmed", command.AttemptId);
        var previous = original.ProcessingStatus;
        var outcome = original.ApplyReversalConfirmed(reversal.Id, context.Value);
        if (outcome.IsFailure)
        {
            return outcome.Error;
        }

        if (outcome.Value.Kind == TransitionKind.NoChange)
        {
            return Result.Success();
        }

        var ledger = await ExecuteLedgerActionAsync(session, original, outcome.Value.LedgerAction, cancellationToken, reversal.Id);
        if (ledger.IsFailure)
        {
            return ledger.Error;
        }

        var saved = await _transactions.UpdateAsync(session, original, cancellationToken);
        if (saved.IsFailure)
        {
            return saved.Error;
        }

        await EnqueueStatusEventAsync(session, original, previous, outcome.Value, command.Source, cancellationToken);
        return Result.Success();
    }

    /// <summary>
    /// ADR-023: the refund child succeeded. In the same database transaction: post the refund (principal = child amount,
    /// fee from <see cref="RefundFeeCalculator"/> over the original's captured fee components and the principal already
    /// refunded by earlier successful refund children), then move the original to PARTIALLY_REFUNDED or REFUNDED.
    /// The refund is booked against the provider that actually processed the original. A merchant API refund is
    /// authorized by its own child transaction, not by maker-checker (ADR-024).
    /// </summary>
    private async Task<Result> ApplyRefundToOriginalAsync(
        IDatabaseSession session, Transaction original, Transaction refund, ProviderResultCommand command, TransitionContext childContext, CancellationToken cancellationToken)
    {
        if (original.Routing is not { } routing || original.Fees is not { } fees)
        {
            return RansysError.Validation(
                ErrorCodes.InvalidStateTransition, $"Original {original.Id} of refund {refund.Id} was never routed or validated.");
        }

        if (refund.Amount.Currency != original.Amount.Currency)
        {
            return RansysError.Validation(ErrorCodes.CurrencyMismatch, $"Refund {refund.Id} does not use the original's currency definition.", "amount");
        }

        // Successful refund children are exactly the refunds already posted: each one posts in the same database
        // transaction as its success, serialized by the original's row lock held here.
        var siblings = await _transactions.FindChildSummariesAsync(session, original.Id, TransactionType.Refund, cancellationToken);
        var before = siblings.Where(c => c.Id != refund.Id && c.Status == ProcessingStatus.Success).Sum(c => c.Amount);
        var refundedBefore = Money.Create(before, original.Amount.Currency);
        if (refundedBefore.IsFailure)
        {
            return refundedBefore.Error;
        }

        var amounts = RefundFeeCalculator.Calculate(original.Amount, fees, refundedBefore.Value, refund.Amount);
        if (amounts.IsFailure)
        {
            return amounts.Error;
        }

        var posted = await _ledger.PostRefundAsync(
            session,
            new RefundRequest(
                original.Id,
                refund.Id,
                refund.Id.ToString(),
                routing.CurrentProvider.ProviderId,
                amounts.Value.Principal,
                amounts.Value.Fee,
                new RefundAuthorization.MerchantApiRequest(refund.Id, refund.ChannelId, refund.Identity.ClientReference),
                Domain.Ledger.LedgerActor.System),
            cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error;
        }

        var fullyRefunded = before + refund.Amount.Amount == original.Amount.Amount;
        var context = TransitionContext.Create(
            fullyRefunded ? "REFUND_COMPLETED" : "PARTIAL_REFUND_COMPLETED", command.Source, childContext.OccurredAt,
            $"Refund {refund.Id} confirmed", command.AttemptId);
        var previous = original.ProcessingStatus;
        var outcome = original.ApplyRefundCompleted(refund.Id, fullyRefunded, context.Value);
        if (outcome.IsFailure)
        {
            return outcome.Error;
        }

        if (outcome.Value.Kind == TransitionKind.NoChange)
        {
            return Result.Success();
        }

        var saved = await _transactions.UpdateAsync(session, original, cancellationToken);
        if (saved.IsFailure)
        {
            return saved.Error;
        }

        await EnqueueStatusEventAsync(session, original, previous, outcome.Value, command.Source, cancellationToken);
        return Result.Success();
    }

    /// <summary>
    /// ADR-019 (fail closed): the VOID child succeeded. No money moves; the original's reconciliation becomes EXCEPTION
    /// for manual handling. Never mapped to a reversal or refund.
    /// </summary>
    private async Task<Result> ApplyVoidToOriginalAsync(
        IDatabaseSession session, Transaction original, Transaction voidChild, ProviderResultCommand command, TransitionContext childContext, CancellationToken cancellationToken)
    {
        var context = TransitionContext.Create(
            ReasonCodes.VoidConfirmedRequiresReview, command.Source, childContext.OccurredAt, $"VOID {voidChild.Id} confirmed", command.AttemptId);
        var previous = original.ProcessingStatus;
        var outcome = original.RecordVoidConfirmed(voidChild.Id, context.Value);
        if (outcome.IsFailure)
        {
            return outcome.Error;
        }

        if (outcome.Value.Changes.Count == 0)
        {
            return Result.Success();
        }

        var saved = await _transactions.UpdateAsync(session, original, cancellationToken);
        if (saved.IsFailure)
        {
            return saved.Error;
        }

        await EnqueueStatusEventAsync(session, original, previous, outcome.Value, command.Source, cancellationToken);
        return Result.Success();
    }

    private async Task<Result> ExecuteLedgerActionAsync(
        IDatabaseSession session, Transaction transaction, LedgerAction action, CancellationToken cancellationToken, TransactionId? reversalId = null)
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
                LedgerAction.ReversalRelease =>
                    await _ledger.ReleaseReservationAsync(session, new ReleaseRequest(transaction.Id, AsReversal: true), cancellationToken),
                LedgerAction.CompensatingReversal when reversalId is { } reversal =>
                    await _ledger.ReversePostedPaymentAsync(
                        session, new ReversePostedPaymentRequest(transaction.Id, reversal.ToString()), cancellationToken),
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
        IDatabaseSession session, Transaction transaction, ProcessingStatus previous, TransitionOutcome outcome, ChangeSource source, CancellationToken cancellationToken) =>
        _outbox.EnqueueAsync(session, TransactionStatusEvents.Build(transaction, previous, outcome, source, _ids, _clock), cancellationToken);

    private static FinalizationResult Snapshot(TransitionOutcome outcome, Transaction transaction) =>
        new(outcome.Kind, outcome.LedgerAction, transaction.ProcessingStatus, transaction.FinancialStatus, transaction.ReconciliationStatus);
}
