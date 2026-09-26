namespace Ransys.Application;

/// <summary>
/// One open atomic unit of database work (connection + explicit transaction). Everything written through the
/// same session commits or rolls back together (ERD v1.1 §43). Concrete type lives in the persistence layer.
/// </summary>
public interface IDatabaseSession
{
}

/// <summary>Source of the current time; injectable for deterministic tests.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Generates RANSYS identifiers (UUIDv7, ERD v1.1 §5).</summary>
public interface IIdGenerator
{
    Guid NewId();
}

/// <summary>
/// A domain event to publish asynchronously. Written into <c>async.outbox_events</c> in the same database
/// transaction as the financial change it describes (Architecture Spec §38).
/// </summary>
public sealed record OutboxMessage(
    Guid EventId,
    string AggregateType,
    Guid AggregateId,
    string EventType,
    int EventVersion,
    long? SourceVersion,
    object Payload,
    DateTimeOffset CreatedAt);

/// <summary>Transactional outbox writer (delivery is handled by the outbox worker, at least once).</summary>
public interface IOutboxWriter
{
    Task EnqueueAsync(IDatabaseSession session, OutboxMessage message, CancellationToken cancellationToken = default);
}
