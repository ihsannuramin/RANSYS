using Ransys.Application;
using Ransys.Domain;
using Ransys.Domain.Attempts;
using Ransys.Domain.Common;
using Ransys.Domain.Routing;
using Ransys.Domain.Transactions;

namespace Ransys.TransactionCore.Attempts;

/// <summary>Persistence port for <c>core.transaction_attempts</c>. Only Transaction Core writes attempts (ADR-005).</summary>
public interface ITransactionAttemptStore
{
    /// <summary>
    /// Persists an attempt before the provider call. Until its outcome is recorded the row is stored pessimistically
    /// as possibly sent (<c>request_sent = true</c>, <c>outcome_recorded_at IS NULL</c>).
    /// </summary>
    Task InsertStartedAsync(IDatabaseSession session, TransactionAttempt attempt, CancellationToken cancellationToken = default);

    /// <summary>Writes the recorded outcome once. Returns false if an outcome was already stored.</summary>
    Task<bool> RecordOutcomeAsync(
        IDatabaseSession session, TransactionAttempt attempt, DateTimeOffset recordedAt, CancellationToken cancellationToken = default);

    /// <summary>All attempts of a transaction, ordered by attempt number.</summary>
    Task<Result<IReadOnlyList<TransactionAttempt>>> GetByTransactionAsync(
        IDatabaseSession session, TransactionId transactionId, CancellationToken cancellationToken = default);

    /// <summary>Attempts still without an outcome that were created before <paramref name="createdBefore"/>.</summary>
    Task<IReadOnlyList<(AttemptId AttemptId, TransactionId TransactionId)>> FindOutcomeLessAsync(
        IDatabaseSession session, DateTimeOffset createdBefore, int limit, CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates and completes provider attempts (main.md §17, Canonical Data Model §42–46).
/// The caller holds the transaction row lock, which serializes attempt numbering per transaction
/// (<c>UNIQUE (ransys_transaction_id, attempt_no)</c> is the database backstop).
/// </summary>
public sealed class TransactionAttemptService
{
    private readonly ITransactionAttemptStore _store;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;

    public TransactionAttemptService(ITransactionAttemptStore store, IClock clock, IIdGenerator ids)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
    }

    /// <summary>
    /// Authorizes (<see cref="Transaction.AuthorizeAttempt"/>) and persists the next attempt. Must be committed
    /// before the provider is called, so a crash during the call leaves a visible, possibly-sent attempt.
    /// </summary>
    public async Task<Result<TransactionAttempt>> StartAsync(
        IDatabaseSession session,
        Transaction transaction,
        AttemptType attemptType,
        ProviderReference provider,
        string correlationId,
        string traceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var prior = await _store.GetByTransactionAsync(session, transaction.Id, cancellationToken);
        if (prior.IsFailure)
        {
            return prior.Error;
        }

        var authorized = transaction.AuthorizeAttempt(attemptType, provider, [.. prior.Value]);
        if (authorized.IsFailure)
        {
            return authorized.Error;
        }

        var attemptNumber = prior.Value.Count == 0 ? 1 : prior.Value.Max(a => a.AttemptNumber) + 1;
        var attempt = TransactionAttempt.Start(
            new AttemptId(_ids.NewId()), transaction.Id, attemptNumber, attemptType, provider, correlationId, traceId, _clock.UtcNow);
        if (attempt.IsFailure)
        {
            return attempt.Error;
        }

        await _store.InsertStartedAsync(session, attempt.Value, cancellationToken);
        return attempt.Value;
    }

    /// <summary>
    /// Records the adapter-reported outcome exactly once. An identical repeat is a no-op; a different outcome, or one
    /// recorded concurrently by another path, is rejected (attempts are immutable history).
    /// </summary>
    public async Task<Result> RecordOutcomeAsync(
        IDatabaseSession session, TransactionAttempt attempt, AttemptOutcome outcome, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(outcome);

        var alreadyRecorded = attempt.IsOutcomeRecorded;
        var recorded = attempt.RecordOutcome(outcome);
        if (recorded.IsFailure || alreadyRecorded)
        {
            return recorded;
        }

        return await _store.RecordOutcomeAsync(session, attempt, _clock.UtcNow, cancellationToken)
            ? Result.Success()
            : RansysError.Conflict(
                ErrorCodes.AttemptOutcomeAlreadyRecorded,
                $"Attempt {attempt.AttemptNumber} of transaction {attempt.TransactionId} already has a stored outcome.");
    }
}
