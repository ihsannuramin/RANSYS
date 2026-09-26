using Microsoft.Extensions.Options;
using Ransys.Application.Outbox;

namespace Ransys.Workers;

/// <summary>
/// Hosts the brokerless outbox delivery loop (Architecture Spec §39: PostgreSQL outbox + .NET background workers).
/// Several instances can run in parallel; <c>FOR UPDATE SKIP LOCKED</c> claiming keeps them from sharing events.
/// </summary>
public sealed partial class OutboxWorker(
    OutboxProcessor processor,
    IOptions<OutboxOptions> options,
    ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(processor.WorkerId, options.Value.BatchSize);
        while (!stoppingToken.IsCancellationRequested)
        {
            OutboxBatchResult result;
            try
            {
                result = await processor.ProcessBatchAsync(stoppingToken);
                if (result.Dead > 0)
                {
                    // DEAD events need operator attention (alerting hook: main.md §23 / Architecture Spec §54).
                    LogDead(result.Dead);
                }

                if (result.LeaseLost > 0)
                {
                    LogLeaseLost(result.LeaseLost);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Database unavailable etc.: nothing was lost (events stay in the outbox); retry after the poll interval.
                LogBatchFailed(ex);
                result = new OutboxBatchResult(0, 0, 0, 0, 0);
            }

            // A full batch means more events are waiting: continue immediately.
            if (result.Claimed < options.Value.BatchSize)
            {
                await Task.Delay(options.Value.PollInterval, stoppingToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox worker {WorkerId} started (batch {BatchSize})")]
    private partial void LogStarted(string workerId, int batchSize);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Count} outbox events exhausted their delivery attempts and are DEAD")]
    private partial void LogDead(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} outbox events were reclaimed by another worker after lease expiry")]
    private partial void LogLeaseLost(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox batch failed")]
    private partial void LogBatchFailed(Exception exception);
}
