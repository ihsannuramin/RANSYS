using Dapper;
using Ransys.Application;

namespace Ransys.Persistence.PostgreSql.Outbox;

/// <summary>
/// Inserts outbox rows (<c>async.outbox_events</c>, status PENDING) in the caller's transaction, so a committed
/// financial change can never lose its event (Architecture Spec §38). Delivery is the outbox worker's job.
/// </summary>
public sealed class PostgresOutboxWriter : IOutboxWriter
{
    static PostgresOutboxWriter() => DbValues.EnsureConfigured();

    public async Task EnqueueAsync(IDatabaseSession session, OutboxMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var s = session as PostgresSession
            ?? throw new ArgumentException($"Expected a {nameof(PostgresSession)}.", nameof(session));

        await s.Connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO async.outbox_events
                (event_id, aggregate_type, aggregate_id, event_type, event_version, source_version, payload,
                 status, retry_count, created_at)
            VALUES (@EventId, @AggregateType, @AggregateId, @EventType, @EventVersion, @SourceVersion,
                    CAST(@Payload AS jsonb), 'PENDING', 0, @CreatedAt)
            """,
            new
            {
                message.EventId,
                message.AggregateType,
                message.AggregateId,
                message.EventType,
                message.EventVersion,
                message.SourceVersion,
                Payload = DbValues.ToJson(message.Payload),
                CreatedAt = DbValues.ToDb(message.CreatedAt),
            },
            s.Transaction, cancellationToken: cancellationToken));
    }
}
