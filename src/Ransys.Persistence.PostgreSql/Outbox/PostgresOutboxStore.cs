using Dapper;
using Ransys.Application;
using Ransys.Application.Outbox;

namespace Ransys.Persistence.PostgreSql.Outbox;

/// <summary>
/// Lease-based claiming on <c>async.outbox_events</c> (DDL v1.1: status, locked_by, locked_at, next_retry_at) with one
/// <c>async.outbox_delivery_attempts</c> row per delivery. <c>FOR UPDATE SKIP LOCKED</c> lets many workers claim in
/// parallel without ever claiming the same event (Architecture Spec §39).
/// </summary>
public sealed class PostgresOutboxStore : IOutboxStore
{
    /// <summary>DDL v1.1 <c>error_code varchar(64)</c> / <c>error_message varchar(1000)</c>.</summary>
    private const int MaxErrorCodeLength = 64;
    private const int MaxErrorMessageLength = 1000;

    static PostgresOutboxStore() => DbValues.EnsureConfigured();

    public async Task<IReadOnlyList<OutboxEnvelope>> ClaimBatchAsync(
        IDatabaseSession session, string workerId, DateTimeOffset now, TimeSpan lease, int batchSize, CancellationToken cancellationToken = default)
    {
        var s = PostgresSessionCast.From(session);
        var rows = await s.Connection.QueryAsync<Row>(new CommandDefinition(
            """
            WITH claimable AS (
                SELECT event_id
                FROM async.outbox_events
                WHERE (status = 'PENDING' AND (next_retry_at IS NULL OR next_retry_at <= @Now))
                   OR (status = 'PROCESSING' AND locked_at < @LeaseExpiredBefore)
                ORDER BY created_at, event_id
                LIMIT @BatchSize
                FOR UPDATE SKIP LOCKED)
            UPDATE async.outbox_events e
            SET status = 'PROCESSING', locked_by = @WorkerId, locked_at = @Now
            FROM claimable c
            WHERE e.event_id = c.event_id
            RETURNING e.event_id, e.aggregate_type, e.aggregate_id, e.event_type, e.event_version, e.source_version,
                      e.payload::text AS payload, e.retry_count, e.created_at
            """,
            new
            {
                Now = DbValues.ToDb(now),
                LeaseExpiredBefore = DbValues.ToDb(now - lease),
                BatchSize = batchSize,
                WorkerId = workerId,
            },
            s.Transaction, cancellationToken: cancellationToken));

        return rows
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.EventId)
            .Select(r => new OutboxEnvelope(
                r.EventId, r.AggregateType, r.AggregateId, r.EventType, r.EventVersion, r.SourceVersion, r.Payload,
                r.RetryCount, DbValues.FromDb(r.CreatedAt)))
            .ToList();
    }

    public async Task<bool> MarkPublishedAsync(
        IDatabaseSession session, Guid eventId, string workerId, OutboxDeliveryAttempt attempt, CancellationToken cancellationToken = default)
    {
        var s = PostgresSessionCast.From(session);
        var owned = await s.Connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE async.outbox_events
            SET status = 'PUBLISHED', published_at = @CompletedAt, locked_by = NULL, locked_at = NULL
            WHERE event_id = @EventId AND status = 'PROCESSING' AND locked_by = @WorkerId
            """,
            new { EventId = eventId, WorkerId = workerId, CompletedAt = DbValues.ToDb(attempt.CompletedAt) },
            s.Transaction, cancellationToken: cancellationToken)) == 1;

        if (owned)
        {
            await InsertAttemptAsync(s, eventId, attempt, cancellationToken);
        }

        return owned;
    }

    public async Task<bool> MarkFailedAsync(
        IDatabaseSession session,
        Guid eventId,
        string workerId,
        int retryCount,
        bool dead,
        DateTimeOffset? nextRetryAt,
        OutboxDeliveryAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        var s = PostgresSessionCast.From(session);
        var owned = await s.Connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE async.outbox_events
            SET status = @Status, retry_count = @RetryCount, next_retry_at = @NextRetryAt, locked_by = NULL, locked_at = NULL
            WHERE event_id = @EventId AND status = 'PROCESSING' AND locked_by = @WorkerId
            """,
            new
            {
                EventId = eventId,
                WorkerId = workerId,
                Status = dead ? "DEAD" : "PENDING",
                RetryCount = retryCount,
                NextRetryAt = DbValues.ToDb(nextRetryAt),
            },
            s.Transaction, cancellationToken: cancellationToken)) == 1;

        if (owned)
        {
            await InsertAttemptAsync(s, eventId, attempt, cancellationToken);
        }

        return owned;
    }

    private static Task InsertAttemptAsync(PostgresSession s, Guid eventId, OutboxDeliveryAttempt attempt, CancellationToken cancellationToken) =>
        s.Connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO async.outbox_delivery_attempts
                (delivery_attempt_id, event_id, attempt_no, transport, result, error_code, error_message, started_at, completed_at)
            VALUES (@Id, @EventId,
                    (SELECT COALESCE(MAX(attempt_no), 0) + 1 FROM async.outbox_delivery_attempts WHERE event_id = @EventId),
                    @Transport, @Result, @ErrorCode, @ErrorMessage, @StartedAt, @CompletedAt)
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                EventId = eventId,
                attempt.Transport,
                Result = attempt.Succeeded ? "SUCCESS" : "FAILED",
                ErrorCode = Truncate(attempt.ErrorCode, MaxErrorCodeLength),
                ErrorMessage = Truncate(attempt.ErrorMessage, MaxErrorMessageLength),
                StartedAt = DbValues.ToDb(attempt.StartedAt),
                CompletedAt = DbValues.ToDb(attempt.CompletedAt),
            },
            s.Transaction, cancellationToken: cancellationToken));

    private static string? Truncate(string? value, int max) => value is { Length: > 0 } v && v.Length > max ? v[..max] : value;

    private sealed class Row
    {
        public Guid EventId { get; init; }

        public string AggregateType { get; init; } = "";

        public Guid AggregateId { get; init; }

        public string EventType { get; init; } = "";

        public int EventVersion { get; init; }

        public long? SourceVersion { get; init; }

        public string Payload { get; init; } = "{}";

        public int RetryCount { get; init; }

        public DateTime CreatedAt { get; init; }
    }
}
