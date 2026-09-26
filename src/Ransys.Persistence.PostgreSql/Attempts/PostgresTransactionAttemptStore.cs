using Dapper;
using Ransys.Application;
using Ransys.Domain;
using Ransys.Domain.Attempts;
using Ransys.Domain.Common;
using Ransys.Domain.Routing;
using Ransys.TransactionCore.Attempts;

namespace Ransys.Persistence.PostgreSql.Attempts;

/// <summary>
/// <c>core.transaction_attempts</c> (DDL v1.1 + migration 0004). Written only by Transaction Core (ADR-005).
/// A started attempt is stored as possibly sent until its outcome is recorded; after that the row is immutable.
/// </summary>
public sealed class PostgresTransactionAttemptStore : ITransactionAttemptStore
{
    static PostgresTransactionAttemptStore() => DbValues.EnsureConfigured();

    public async Task InsertStartedAsync(IDatabaseSession session, TransactionAttempt attempt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.IsOutcomeRecorded)
        {
            throw new InvalidOperationException("Only attempts without an outcome can be inserted as started.");
        }

        var s = PostgresSessionCast.From(session);

        // Pessimistic defaults (ADR-005): any reader that ignores outcome_recorded_at still sees "possibly sent".
        await s.Connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO core.transaction_attempts
                (transaction_attempt_id, ransys_transaction_id, attempt_no, attempt_type, provider_id,
                 request_sent, transport_status, correlation_id, trace_id, created_at, metadata, outcome_recorded_at)
            VALUES (@Id, @TransactionId, @AttemptNo, @AttemptType, @ProviderId,
                    true, 'SENT', @CorrelationId, @TraceId, @CreatedAt, '{}'::jsonb, NULL)
            """,
            new
            {
                Id = attempt.Id.Value,
                TransactionId = attempt.TransactionId.Value,
                AttemptNo = attempt.AttemptNumber,
                AttemptType = CanonicalCodes.AttemptType.ToCode(attempt.AttemptType),
                ProviderId = attempt.Provider.ProviderId.Value,
                attempt.CorrelationId,
                attempt.TraceId,
                CreatedAt = DbValues.ToDb(attempt.CreatedAt),
            },
            s.Transaction, cancellationToken: cancellationToken));
    }

    public async Task<bool> RecordOutcomeAsync(
        IDatabaseSession session, TransactionAttempt attempt, DateTimeOffset recordedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var outcome = attempt.Outcome ?? throw new InvalidOperationException("The attempt has no outcome to record.");
        var s = PostgresSessionCast.From(session);

        var affected = await s.Connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE core.transaction_attempts SET
                request_sent = @RequestSent,
                transport_status = @TransportStatus,
                provider_transaction_status = @ProviderTransactionStatus,
                ransys_response_code = @RansysResponseCode,
                provider_response_code = @ProviderResponseCode,
                provider_response_message = @ProviderResponseMessage,
                provider_reference = @ProviderReference,
                provider_stan = @ProviderStan,
                provider_rrn = @ProviderRrn,
                latency_ms = @LatencyMs,
                raw_request_reference = @RawRequest,
                raw_response_reference = @RawResponse,
                provider_sent_at = @ProviderSentAt,
                provider_response_at = @ProviderResponseAt,
                metadata = CAST(@Metadata AS jsonb),
                outcome_recorded_at = @RecordedAt
            WHERE transaction_attempt_id = @Id AND outcome_recorded_at IS NULL
            """,
            new
            {
                Id = attempt.Id.Value,
                outcome.RequestSent,
                TransportStatus = CanonicalCodes.TransportStatus.ToCode(outcome.TransportStatus),
                outcome.ProviderTransactionStatus,
                outcome.RansysResponseCode,
                outcome.ProviderResponseCode,
                outcome.ProviderResponseMessage,
                outcome.ProviderReference,
                outcome.ProviderStan,
                outcome.ProviderRrn,
                LatencyMs = outcome.Latency is { } latency ? (int?)Math.Round(latency.TotalMilliseconds) : null,
                RawRequest = outcome.RawMessages.RequestUri,
                RawResponse = outcome.RawMessages.ResponseUri,
                ProviderSentAt = DbValues.ToDb(outcome.ProviderSentAt),
                ProviderResponseAt = DbValues.ToDb(outcome.ProviderResponseAt),
                Metadata = DbValues.MetadataToJson(outcome.Metadata),
                RecordedAt = DbValues.ToDb(recordedAt),
            },
            s.Transaction, cancellationToken: cancellationToken));
        return affected == 1;
    }

    public async Task<Result<IReadOnlyList<TransactionAttempt>>> GetByTransactionAsync(
        IDatabaseSession session, TransactionId transactionId, CancellationToken cancellationToken = default)
    {
        var s = PostgresSessionCast.From(session);
        var rows = await s.Connection.QueryAsync<AttemptRow>(new CommandDefinition(
            """
            SELECT a.transaction_attempt_id, a.ransys_transaction_id, a.attempt_no, a.attempt_type, a.provider_id,
                   p.provider_code, p.adapter_service_name, a.request_sent, a.transport_status,
                   a.provider_transaction_status, a.ransys_response_code, a.provider_response_code,
                   a.provider_response_message, a.provider_reference, a.provider_stan, a.provider_rrn, a.latency_ms,
                   a.raw_request_reference, a.raw_response_reference, a.correlation_id, a.trace_id,
                   a.provider_sent_at, a.provider_response_at, a.created_at, a.metadata::text AS metadata,
                   a.outcome_recorded_at
            FROM core.transaction_attempts a
            JOIN integration.providers p ON p.provider_id = a.provider_id
            WHERE a.ransys_transaction_id = @Id
            ORDER BY a.attempt_no
            """,
            new { Id = transactionId.Value }, s.Transaction, cancellationToken: cancellationToken));

        var attempts = new List<TransactionAttempt>();
        foreach (var row in rows)
        {
            var attempt = ToAttempt(row);
            if (attempt.IsFailure)
            {
                return new RansysError(
                    ErrorCodes.PersistedStateInvalid, ErrorCategory.Internal,
                    $"Persisted attempt {row.TransactionAttemptId} cannot be loaded: {attempt.Error}.");
            }

            attempts.Add(attempt.Value);
        }

        return attempts;
    }

    public async Task<IReadOnlyList<(AttemptId AttemptId, TransactionId TransactionId)>> FindOutcomeLessAsync(
        IDatabaseSession session, DateTimeOffset createdBefore, int limit, CancellationToken cancellationToken = default)
    {
        var s = PostgresSessionCast.From(session);
        var rows = await s.Connection.QueryAsync<(Guid AttemptId, Guid TransactionId)>(new CommandDefinition(
            """
            SELECT transaction_attempt_id, ransys_transaction_id
            FROM core.transaction_attempts
            WHERE outcome_recorded_at IS NULL AND created_at < @CreatedBefore
            ORDER BY created_at
            LIMIT @Limit
            """,
            new { CreatedBefore = DbValues.ToDb(createdBefore), Limit = limit }, s.Transaction, cancellationToken: cancellationToken));
        return rows.Select(r => (new AttemptId(r.AttemptId), new TransactionId(r.TransactionId))).ToList();
    }

    private static Result<TransactionAttempt> ToAttempt(AttemptRow row)
    {
        if (!CanonicalCodes.AttemptType.TryParse(row.AttemptType, out var attemptType))
        {
            return RansysError.Validation(ErrorCodes.OutOfRange, $"Unknown attempt type '{row.AttemptType}'.", "attemptType");
        }

        var provider = ProviderReference.Create(new ProviderId(row.ProviderId), row.ProviderCode, row.AdapterServiceName);
        if (provider.IsFailure)
        {
            return provider.Error;
        }

        AttemptOutcome? outcome = null;
        if (row.OutcomeRecordedAt is not null)
        {
            var built = ToOutcome(row);
            if (built.IsFailure)
            {
                return built.Error;
            }

            outcome = built.Value;
        }

        return TransactionAttempt.Rehydrate(
            new AttemptId(row.TransactionAttemptId), new TransactionId(row.RansysTransactionId), row.AttemptNo, attemptType,
            provider.Value, row.CorrelationId, row.TraceId, DbValues.FromDb(row.CreatedAt), outcome);
    }

    private static Result<AttemptOutcome> ToOutcome(AttemptRow row)
    {
        if (!CanonicalCodes.TransportStatus.TryParse(row.TransportStatus, out var transport))
        {
            return RansysError.Validation(ErrorCodes.OutOfRange, $"Unknown transport status '{row.TransportStatus}'.", "transportStatus");
        }

        var raw = RawMessageReferences.Create(row.RawRequestReference, row.RawResponseReference);
        var metadata = DbValues.MetadataFromJson(row.Metadata);
        if (raw.IsFailure || metadata.IsFailure)
        {
            return raw.IsFailure ? raw.Error : metadata.Error;
        }

        return AttemptOutcome.Create(
            row.RequestSent,
            transport,
            row.ProviderTransactionStatus,
            row.RansysResponseCode,
            row.ProviderResponseCode,
            row.ProviderResponseMessage,
            row.ProviderReference,
            row.ProviderStan,
            row.ProviderRrn,
            row.LatencyMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
            raw.Value,
            DbValues.FromDb(row.ProviderSentAt),
            DbValues.FromDb(row.ProviderResponseAt),
            metadata.Value);
    }

    private sealed class AttemptRow
    {
        public Guid TransactionAttemptId { get; init; }

        public Guid RansysTransactionId { get; init; }

        public int AttemptNo { get; init; }

        public string AttemptType { get; init; } = "";

        public Guid ProviderId { get; init; }

        public string ProviderCode { get; init; } = "";

        public string AdapterServiceName { get; init; } = "";

        public bool RequestSent { get; init; }

        public string TransportStatus { get; init; } = "";

        public string? ProviderTransactionStatus { get; init; }

        public string? RansysResponseCode { get; init; }

        public string? ProviderResponseCode { get; init; }

        public string? ProviderResponseMessage { get; init; }

        public string? ProviderReference { get; init; }

        public string? ProviderStan { get; init; }

        public string? ProviderRrn { get; init; }

        public int? LatencyMs { get; init; }

        public string? RawRequestReference { get; init; }

        public string? RawResponseReference { get; init; }

        public string CorrelationId { get; init; } = "";

        public string TraceId { get; init; } = "";

        public DateTime? ProviderSentAt { get; init; }

        public DateTime? ProviderResponseAt { get; init; }

        public DateTime CreatedAt { get; init; }

        public string Metadata { get; init; } = "{}";

        public DateTime? OutcomeRecordedAt { get; init; }
    }
}
