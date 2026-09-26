namespace Ransys.Application.Outbox;

/// <summary>A claimed outbox event handed to the publisher. <see cref="EventId"/> is the consumer dedup key.</summary>
public sealed record OutboxEnvelope(
    Guid EventId,
    string AggregateType,
    Guid AggregateId,
    string EventType,
    int EventVersion,
    long? SourceVersion,
    string PayloadJson,
    int RetryCount,
    DateTimeOffset CreatedAt);

/// <summary>Result of one publish call.</summary>
public sealed record PublishResult(bool Succeeded, string? ErrorCode, string? ErrorMessage)
{
    public static PublishResult Success { get; } = new(true, null, null);

    public static PublishResult Failure(string errorCode, string errorMessage) => new(false, errorCode, errorMessage);
}

/// <summary>
/// Delivers events to their consumers (Backoffice projection, optional RabbitMQ, …). Delivery is at least once
/// (Architecture Spec §40): the same event can be published more than once, so consumers deduplicate by
/// <see cref="OutboxEnvelope.EventId"/> and order by <see cref="OutboxEnvelope.SourceVersion"/>.
/// </summary>
public interface IOutboxPublisher
{
    /// <summary>Transport name recorded in <c>async.outbox_delivery_attempts.transport</c>.</summary>
    string Transport { get; }

    Task<PublishResult> PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>One row of <c>async.outbox_delivery_attempts</c>.</summary>
public sealed record OutboxDeliveryAttempt(
    string Transport,
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

/// <summary>Persistence port for <c>async.outbox_events</c> (lease-based claiming).</summary>
public interface IOutboxStore
{
    /// <summary>
    /// Claims up to <paramref name="batchSize"/> due events (<c>FOR UPDATE SKIP LOCKED</c>): PENDING events whose retry
    /// time has come, plus PROCESSING events whose lease expired (their worker died). Claimed rows become PROCESSING and
    /// are leased to <paramref name="workerId"/>.
    /// </summary>
    Task<IReadOnlyList<OutboxEnvelope>> ClaimBatchAsync(
        IDatabaseSession session, string workerId, DateTimeOffset now, TimeSpan lease, int batchSize, CancellationToken cancellationToken = default);

    /// <summary>Marks the event PUBLISHED if <paramref name="workerId"/> still holds the lease. Returns false if the lease was lost.</summary>
    Task<bool> MarkPublishedAsync(
        IDatabaseSession session, Guid eventId, string workerId, OutboxDeliveryAttempt attempt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a failed delivery: back to PENDING with <paramref name="nextRetryAt"/>, or DEAD when <paramref name="dead"/>.
    /// Returns false if the lease was lost.
    /// </summary>
    Task<bool> MarkFailedAsync(
        IDatabaseSession session,
        Guid eventId,
        string workerId,
        int retryCount,
        bool dead,
        DateTimeOffset? nextRetryAt,
        OutboxDeliveryAttempt attempt,
        CancellationToken cancellationToken = default);
}
