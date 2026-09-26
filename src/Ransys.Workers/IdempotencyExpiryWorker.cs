using Microsoft.Extensions.Options;
using Npgsql;
using Ransys.Persistence.PostgreSql;
using Ransys.TransactionCore.Idempotency;

namespace Ransys.Workers;

public sealed class IdempotencyExpiryOptions
{
    public const string Section = "Ransys:IdempotencyExpiry";

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    public int BatchSize { get; set; } = 500;
}

/// <summary>
/// ADR-009 periodic sweep: marks idempotency claims past their 24-hour window inactive, in bounded batches.
/// Request handling also expires stale claims lazily, so a delayed sweep never affects correctness.
/// </summary>
public sealed partial class IdempotencyExpiryWorker(
    NpgsqlDataSource dataSource,
    IdempotencyService idempotency,
    IOptions<IdempotencyExpiryOptions> options,
    ILogger<IdempotencyExpiryWorker> logger) : BackgroundService
{
    /// <summary>Expires one batch in its own transaction. Returns the number of records expired.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var session = await PostgresSession.BeginAsync(dataSource, cancellationToken: cancellationToken);
        var expired = await idempotency.ExpireDueAsync(session, options.Value.BatchSize, cancellationToken);
        await session.CommitAsync(cancellationToken);
        return expired;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int expired;
            try
            {
                expired = await RunOnceAsync(stoppingToken);
                if (expired > 0)
                {
                    LogExpired(expired);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed sweep is retried on the next interval; the lazy expiry path keeps requests correct.
                LogSweepFailed(ex);
                expired = 0;
            }

            // A full batch means more work is waiting: continue immediately.
            if (expired < options.Value.BatchSize)
            {
                await Task.Delay(options.Value.Interval, stoppingToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Expired {Count} idempotency records")]
    private partial void LogExpired(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Idempotency expiry sweep failed")]
    private partial void LogSweepFailed(Exception exception);
}
