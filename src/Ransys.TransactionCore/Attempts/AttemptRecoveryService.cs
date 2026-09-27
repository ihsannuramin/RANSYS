using System.Text.Json;
using Ransys.Application;
using Ransys.Domain;
using Ransys.Domain.Attempts;
using Ransys.Domain.Common;
using Ransys.Domain.Ledger;
using Ransys.Domain.Transactions;
using Ransys.Ledger;
using Ransys.TransactionCore.Finalization;

namespace Ransys.TransactionCore.Attempts;

public enum AttemptRecoveryOutcome
{
    /// <summary>The attempt was marked possibly-sent and the transaction moved to (or stayed in) IN_DOUBT.</summary>
    MarkedInDoubt,

    /// <summary>The attempt got a recorded unknown outcome; the transaction state was already final or unaffected.</summary>
    AttemptClosed,

    /// <summary>Another path recorded the outcome first; nothing changed.</summary>
    AlreadyResolved,
}

/// <summary>
/// ADR-005 crash recovery: an attempt that never got an outcome (e.g. Transaction Core restarted during the
/// provider call) is treated as <em>possibly sent</em>. It is closed with an explicit unknown outcome
/// (<c>request_sent = true</c>, TIMEOUT), and the transaction goes IN_DOUBT with its reservation held.
/// It is never failed over and never released automatically; resolution continues via status check,
/// callback, reversal or reconciliation.
/// </summary>
public sealed class AttemptRecoveryService
{
    /// <summary>Metadata marker on synthetic outcomes (namespaced per Canonical Data Model §41).</summary>
    public const string OutcomeSourceKey = "extension.recovery.outcomeSource";

    private readonly ITransactionRepository _transactions;
    private readonly ITransactionAttemptStore _attempts;
    private readonly TransactionAttemptService _attemptService;
    private readonly ILedgerPostingService _ledger;
    private readonly IOutboxWriter _outbox;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;

    public AttemptRecoveryService(
        ITransactionRepository transactions,
        ITransactionAttemptStore attempts,
        TransactionAttemptService attemptService,
        ILedgerPostingService ledger,
        IOutboxWriter outbox,
        IClock clock,
        IIdGenerator ids)
    {
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _attempts = attempts ?? throw new ArgumentNullException(nameof(attempts));
        _attemptService = attemptService ?? throw new ArgumentNullException(nameof(attemptService));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
    }

    /// <summary>
    /// Candidates: attempts without outcome created before now − <paramref name="minimumAge"/>. The age must exceed the
    /// longest provider timeout so that in-flight calls are never touched.
    /// </summary>
    public Task<IReadOnlyList<(AttemptId AttemptId, TransactionId TransactionId)>> FindCandidatesAsync(
        IDatabaseSession session, TimeSpan minimumAge, int limit, CancellationToken cancellationToken = default) =>
        _attempts.FindOutcomeLessAsync(session, _clock.UtcNow - minimumAge, limit, cancellationToken);

    /// <summary>Recovers one attempt inside <paramref name="session"/> (lock order: transaction → reservation).</summary>
    public async Task<Result<AttemptRecoveryOutcome>> RecoverAsync(
        IDatabaseSession session, TransactionId transactionId, AttemptId attemptId, CancellationToken cancellationToken = default)
    {
        var loaded = await _transactions.GetAsync(session, transactionId, forUpdate: true, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        if (loaded.Value is not { } transaction)
        {
            return RansysError.Validation(ErrorCodes.AttemptNotFound, $"Transaction {transactionId} does not exist.");
        }

        var attempts = await _attempts.GetByTransactionAsync(session, transactionId, cancellationToken);
        if (attempts.IsFailure)
        {
            return attempts.Error;
        }

        var attempt = attempts.Value.SingleOrDefault(a => a.Id == attemptId);
        if (attempt is null)
        {
            return RansysError.Validation(ErrorCodes.AttemptNotFound, $"Attempt {attemptId} does not exist.");
        }

        if (attempt.IsOutcomeRecorded)
        {
            return AttemptRecoveryOutcome.AlreadyResolved;
        }

        var unknown = AttemptOutcome.Create(
            requestSent: true,
            TransportStatus.Timeout,
            metadata: ExtensionMetadata.Create([
                new KeyValuePair<string, JsonElement>(OutcomeSourceKey, JsonSerializer.SerializeToElement("SYSTEM_RECOVERY")),
            ]).Value).Value;
        var closed = await _attemptService.RecordOutcomeAsync(session, attempt, unknown, cancellationToken);
        if (closed.IsFailure)
        {
            return closed.Error;
        }

        // Status checks and advices do not change the transaction truth; only the primary/reversal request does.
        if (attempt.AttemptType is AttemptType.StatusCheck or AttemptType.Advice)
        {
            return AttemptRecoveryOutcome.AttemptClosed;
        }

        var previous = transaction.ProcessingStatus;
        var context = TransitionContext.Create(
            ReasonCodes.AttemptOutcomeUnknown, ChangeSource.SystemRecovery, _clock.UtcNow, attemptId: attempt.Id).Value;
        var inDoubt = transaction.MarkInDoubt(context);
        if (inDoubt.IsFailure)
        {
            // The transaction already reached a state this attempt cannot change (e.g. a callback resolved it).
            return AttemptRecoveryOutcome.AttemptClosed;
        }

        if (transaction.FinancialStatus == FinancialStatus.Reserved)
        {
            var hold = await _ledger.ChangeHoldReasonAsync(session, transactionId, "IN_DOUBT", cancellationToken);
            if (hold.IsFailure)
            {
                return hold.Error;
            }
        }

        var saved = await _transactions.UpdateAsync(session, transaction, cancellationToken);
        if (saved.IsFailure)
        {
            return saved.Error;
        }

        // Same rule as finalization: a NoChange outcome (transaction was already IN_DOUBT) reported nothing new,
        // so no second status event is emitted for it.
        if (inDoubt.Value.Kind != TransitionKind.NoChange)
        {
            await _outbox.EnqueueAsync(
                session, TransactionStatusEvents.Build(transaction, previous, inDoubt.Value, ChangeSource.SystemRecovery, _ids, _clock), cancellationToken);
        }

        return AttemptRecoveryOutcome.MarkedInDoubt;
    }
}
