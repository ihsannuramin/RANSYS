using Microsoft.Extensions.Logging;
using Ransys.Application.Outbox;

namespace Ransys.Infrastructure;

/// <summary>
/// PLACEHOLDER publisher for Phase 1 (brokerless, no consumer yet). Logs the event identity only; the payload is not
/// logged because it may contain business data (main.md §23). Replaced by the Backoffice projection consumer
/// and/or an optional RabbitMQ publisher; the Transaction Core never depends on either.
/// </summary>
public sealed partial class LoggingOutboxPublisher(ILogger<LoggingOutboxPublisher> logger) : IOutboxPublisher
{
    public string Transport => "LOG";

    public Task<PublishResult> PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        LogPublished(envelope.EventType, envelope.EventVersion, envelope.EventId, envelope.AggregateType, envelope.AggregateId, envelope.SourceVersion);
        return Task.FromResult(PublishResult.Success);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Outbox event {EventType} v{EventVersion} {EventId} for {AggregateType} {AggregateId} (source version {SourceVersion})")]
    private partial void LogPublished(string eventType, int eventVersion, Guid eventId, string aggregateType, Guid aggregateId, long? sourceVersion);
}
