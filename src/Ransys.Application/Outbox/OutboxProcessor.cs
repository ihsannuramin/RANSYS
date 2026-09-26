namespace Ransys.Application.Outbox;

public sealed class OutboxOptions
{
    public const string Section = "Ransys:Outbox";

    public int BatchSize { get; set; } = 100;

    /// <summary>A claimed event not completed within this time is reclaimed by another worker.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Deliveries before an event becomes DEAD (needs operator attention).</summary>
    public int MaxAttempts { get; set; } = 10;

    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
}

public sealed record OutboxBatchResult(int Claimed, int Published, int Retrying, int Dead, int LeaseLost);

/// <summary>
/// Brokerless transactional outbox delivery (Architecture Spec §38–40, main.md §18). Each batch:
/// claim in a short transaction → publish outside any transaction → complete each event in its own short
/// transaction. No database lock is held while publishing. Delivery is at least once.
/// </summary>
public sealed class OutboxProcessor
{
    private readonly IDatabaseSessionFactory _sessions;
    private readonly IOutboxStore _store;
    private readonly IOutboxPublisher _publisher;
    private readonly IClock _clock;
    private readonly OutboxOptions _options;
    private readonly string _workerId;

    public OutboxProcessor(
        IDatabaseSessionFactory sessions,
        IOutboxStore store,
        IOutboxPublisher publisher,
        IClock clock,
        OutboxOptions options,
        string workerId)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        _workerId = workerId;
    }

    public string WorkerId => _workerId;

    public async Task<OutboxBatchResult> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OutboxEnvelope> claimed;
        await using (var claim = await _sessions.BeginAsync(cancellationToken))
        {
            claimed = await _store.ClaimBatchAsync(
                claim, _workerId, _clock.UtcNow, _options.LeaseDuration, _options.BatchSize, cancellationToken);
            await claim.CommitAsync(cancellationToken);
        }

        int published = 0, retrying = 0, dead = 0, leaseLost = 0;
        foreach (var envelope in claimed)
        {
            var startedAt = _clock.UtcNow;
            var result = await PublishSafelyAsync(envelope, cancellationToken);
            var attempt = new OutboxDeliveryAttempt(
                _publisher.Transport, result.Succeeded, result.ErrorCode, result.ErrorMessage, startedAt, _clock.UtcNow);

            await using var complete = await _sessions.BeginAsync(cancellationToken);
            bool owned;
            if (result.Succeeded)
            {
                owned = await _store.MarkPublishedAsync(complete, envelope.EventId, _workerId, attempt, cancellationToken);
                published += owned ? 1 : 0;
            }
            else
            {
                var retryCount = envelope.RetryCount + 1;
                var isDead = retryCount >= _options.MaxAttempts;
                DateTimeOffset? nextRetryAt = isDead ? null : attempt.CompletedAt + RetryDelay(retryCount);
                owned = await _store.MarkFailedAsync(
                    complete, envelope.EventId, _workerId, retryCount, isDead, nextRetryAt, attempt, cancellationToken);
                if (owned)
                {
                    dead += isDead ? 1 : 0;
                    retrying += isDead ? 0 : 1;
                }
            }

            // Lease lost: another worker reclaimed the event after our lease expired and now owns its state.
            leaseLost += owned ? 0 : 1;
            await complete.CommitAsync(cancellationToken);
        }

        return new OutboxBatchResult(claimed.Count, published, retrying, dead, leaseLost);
    }

    /// <summary>Exponential backoff: base × 2^(retry − 1), capped.</summary>
    public TimeSpan RetryDelay(int retryCount)
    {
        var exponent = Math.Min(retryCount - 1, 30);
        var delay = _options.BaseRetryDelay * Math.Pow(2, exponent);
        return delay < _options.MaxRetryDelay ? delay : _options.MaxRetryDelay;
    }

    // A publisher exception or timeout is a failed delivery to retry, never a lost event.
    private async Task<PublishResult> PublishSafelyAsync(OutboxEnvelope envelope, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.PublishTimeout);
        try
        {
            return await _publisher.PublishAsync(envelope, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PublishResult.Failure("PUBLISH_TIMEOUT", $"Publish exceeded {_options.PublishTimeout}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PublishResult.Failure("PUBLISH_EXCEPTION", $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
