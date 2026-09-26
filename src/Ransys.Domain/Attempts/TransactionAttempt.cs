using Ransys.Domain.Common;
using Ransys.Domain.Routing;

namespace Ransys.Domain.Attempts;

/// <summary>
/// One provider call for a transaction: the network truth, separate from the transaction's processing truth
/// (Canonical Data Model §42–45, State Transition Matrix §64).
/// <para>
/// Safety rule (ADR-005): an attempt is created <em>before</em> the provider call. Until an outcome is recorded,
/// RANSYS cannot know whether the request left the process, so the attempt is treated as
/// <see cref="MayHaveReachedProvider">possibly sent</see>. Only an explicitly recorded "not sent" outcome
/// permits failover.
/// </para>
/// Attempts are immutable operational history: the outcome is recorded once.
/// </summary>
public sealed class TransactionAttempt
{
    /// <summary>DDL v1.1 <c>correlation_id</c> / <c>trace_id varchar(128)</c>.</summary>
    public const int MaxCorrelationLength = 128;

    private TransactionAttempt(
        AttemptId id,
        TransactionId transactionId,
        int attemptNumber,
        AttemptType attemptType,
        ProviderReference provider,
        string correlationId,
        string traceId,
        DateTimeOffset createdAt,
        AttemptOutcome? outcome)
    {
        Id = id;
        TransactionId = transactionId;
        AttemptNumber = attemptNumber;
        AttemptType = attemptType;
        Provider = provider;
        CorrelationId = correlationId;
        TraceId = traceId;
        CreatedAt = createdAt;
        Outcome = outcome;
    }

    public AttemptId Id { get; }

    public TransactionId TransactionId { get; }

    /// <summary>1-based, unique per transaction.</summary>
    public int AttemptNumber { get; }

    public AttemptType AttemptType { get; }

    public ProviderReference Provider { get; }

    public string CorrelationId { get; }

    public string TraceId { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>Null until the provider call completed (or was proven not to have happened).</summary>
    public AttemptOutcome? Outcome { get; private set; }

    public bool IsOutcomeRecorded => Outcome is not null;

    /// <summary>True only when a recorded outcome proves the request was never sent.</summary>
    public bool ProvesRequestNotSent => Outcome?.ProvesRequestNotSent ?? false;

    /// <summary>
    /// Conservative view used for failover and recovery decisions: anything not proven unsent,
    /// including an attempt without a recorded outcome, may have reached the provider.
    /// </summary>
    public bool MayHaveReachedProvider => !ProvesRequestNotSent;

    public static Result<TransactionAttempt> Start(
        AttemptId id,
        TransactionId transactionId,
        int attemptNumber,
        AttemptType attemptType,
        ProviderReference provider,
        string? correlationId,
        string? traceId,
        DateTimeOffset createdAt) =>
        Create(id, transactionId, attemptNumber, attemptType, provider, correlationId, traceId, createdAt, outcome: null);

    /// <summary>Rebuilds an attempt from persisted state, re-validating every field.</summary>
    public static Result<TransactionAttempt> Rehydrate(
        AttemptId id,
        TransactionId transactionId,
        int attemptNumber,
        AttemptType attemptType,
        ProviderReference provider,
        string? correlationId,
        string? traceId,
        DateTimeOffset createdAt,
        AttemptOutcome? outcome) =>
        Create(id, transactionId, attemptNumber, attemptType, provider, correlationId, traceId, createdAt, outcome);

    /// <summary>
    /// Records the call outcome once. Recording an identical outcome again is an idempotent no-op;
    /// a different outcome is rejected because attempts are immutable history.
    /// </summary>
    public Result RecordOutcome(AttemptOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (Outcome is null)
        {
            Outcome = outcome;
            return Result.Success();
        }

        return Outcome == outcome
            ? Result.Success()
            : RansysError.Conflict(
                ErrorCodes.AttemptOutcomeAlreadyRecorded,
                $"Attempt {AttemptNumber} of transaction {TransactionId} already has a different recorded outcome.");
    }

    private static Result<TransactionAttempt> Create(
        AttemptId id,
        TransactionId transactionId,
        int attemptNumber,
        AttemptType attemptType,
        ProviderReference provider,
        string? correlationId,
        string? traceId,
        DateTimeOffset createdAt,
        AttemptOutcome? outcome)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (attemptNumber <= 0)
        {
            return RansysError.Validation(ErrorCodes.OutOfRange, "Attempt number must be positive.", "attemptNumber");
        }

        if (!Enum.IsDefined(attemptType))
        {
            return RansysError.Validation(ErrorCodes.OutOfRange, "Unknown attempt type.", "attemptType");
        }

        var error = Text.FirstError(
            Text.Required(correlationId, "correlationId", MaxCorrelationLength),
            Text.Required(traceId, "traceId", MaxCorrelationLength));
        if (error is not null)
        {
            return error;
        }

        return new TransactionAttempt(
            id, transactionId, attemptNumber, attemptType, provider, correlationId!, traceId!, createdAt, outcome);
    }
}
