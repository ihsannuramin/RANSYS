using Ransys.Application;
using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Transactions;

namespace Ransys.TransactionCore.Idempotency;

/// <summary>One row of <c>core.idempotency_records</c>.</summary>
public sealed record IdempotencyRecord(
    Guid Id,
    ChannelId ChannelId,
    string ClientReference,
    string? IdempotencyKey,
    TransactionFingerprint Fingerprint,
    TransactionId TransactionId,
    bool Active,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public enum IdempotencyOutcome
{
    /// <summary>The reference was claimed; the new transaction was created in this session.</summary>
    New,

    /// <summary>Idempotent retry: same reference and same fingerprint; return the existing transaction.</summary>
    ExistingTransaction,
}

public sealed record IdempotencyDecision(IdempotencyOutcome Outcome, TransactionId TransactionId);

/// <summary>Persistence port for <c>core.idempotency_records</c>.</summary>
public interface IIdempotencyStore
{
    /// <summary>The active record for channel + client reference, if any (read without lock).</summary>
    Task<Result<IdempotencyRecord?>> FindActiveAsync(
        IDatabaseSession session, ChannelId channelId, string clientReference, CancellationToken cancellationToken);

    /// <summary>Marks an active, already-expired record inactive. Returns false if it was not (or no longer) active.</summary>
    Task<bool> ExpireAsync(IDatabaseSession session, Guid recordId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts an active record. Returns false when another active record for the same channel + reference exists
    /// (partial unique index). The failed statement leaves the transaction aborted: callers must roll back to a savepoint.
    /// </summary>
    Task<bool> TryInsertAsync(IDatabaseSession session, IdempotencyRecord record, CancellationToken cancellationToken);

    /// <summary>Expires up to <paramref name="batchSize"/> due records (ADR-009 sweep). Returns the number expired.</summary>
    Task<int> ExpireDueAsync(IDatabaseSession session, DateTimeOffset now, int batchSize, CancellationToken cancellationToken);
}
