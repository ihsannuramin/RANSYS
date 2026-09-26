using Ransys.Application;
using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Transactions;

namespace Ransys.TransactionCore.Idempotency;

/// <summary>
/// Business idempotency (PRD §11, Architecture Spec §7–8, Sequence Pack SD-05, ADR-009):
/// <list type="bullet">
/// <item>scope: channel + client reference, active window 24 hours;</item>
/// <item>same reference + same fingerprint → the existing transaction (idempotent retry);</item>
/// <item>same reference + different fingerprint → <see cref="ErrorCodes.DuplicateReferenceConflict"/>; nothing is processed.</item>
/// </list>
/// This is not the security nonce: anti-replay is handled by the authentication layer.
/// </summary>
public sealed class IdempotencyService
{
    /// <summary>PRD §11.1 baseline.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private const string ClaimSavepoint = "idempotency_claim";
    private const int MaxClaimAttempts = 3;

    private readonly IIdempotencyStore _store;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;

    public IdempotencyService(IIdempotencyStore store, IClock clock, IIdGenerator ids)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
    }

    /// <summary>
    /// Claims channel + client reference for a new transaction, atomically with its creation.
    /// <paramref name="createTransaction"/> must insert the <c>core.transactions</c> row in <paramref name="session"/>;
    /// it runs only when no active claim exists. If a concurrent request wins the unique-index race, everything
    /// done by <paramref name="createTransaction"/> is rolled back to a savepoint and the winner's claim is evaluated
    /// instead. In that case the caller must discard the aggregate it tried to create.
    /// </summary>
    public async Task<Result<IdempotencyDecision>> ClaimAsync(
        IDatabaseSession session,
        ChannelId channelId,
        TransactionIdentity identity,
        Func<CancellationToken, Task<Result>> createTransaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(createTransaction);

        for (var attempt = 1; attempt <= MaxClaimAttempts; attempt++)
        {
            var now = _clock.UtcNow;
            var existing = await ResolveExistingAsync(session, channelId, identity, now, cancellationToken);
            if (existing.IsFailure)
            {
                return existing.Error;
            }

            if (existing.Value is { } decision)
            {
                return decision;
            }

            await session.CreateSavepointAsync(ClaimSavepoint, cancellationToken);

            var created = await createTransaction(cancellationToken);
            if (created.IsFailure)
            {
                await session.RollbackToSavepointAsync(ClaimSavepoint, cancellationToken);
                return created.Error;
            }

            var record = new IdempotencyRecord(
                _ids.NewId(), channelId, identity.ClientReference, identity.IdempotencyKey, identity.Fingerprint,
                identity.RansysTransactionId, Active: true, now, now.Add(Window));

            if (await _store.TryInsertAsync(session, record, cancellationToken))
            {
                await session.ReleaseSavepointAsync(ClaimSavepoint, cancellationToken);
                return new IdempotencyDecision(IdempotencyOutcome.New, identity.RansysTransactionId);
            }

            // Lost the race to a concurrent request with the same reference: undo our transaction row and
            // evaluate the winner's claim on the next attempt.
            await session.RollbackToSavepointAsync(ClaimSavepoint, cancellationToken);
        }

        return RansysError.Conflict(
            ErrorCodes.DuplicateReferenceConflict,
            $"Client reference '{identity.ClientReference}' could not be claimed after {MaxClaimAttempts} attempts.");
    }

    /// <summary>ADR-009 periodic sweep: keeps the active-reference index small. Correctness does not depend on it.</summary>
    public Task<int> ExpireDueAsync(IDatabaseSession session, int batchSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        return _store.ExpireDueAsync(session, _clock.UtcNow, batchSize, cancellationToken);
    }

    /// <summary>
    /// Returns a decision when an active, unexpired claim exists; null when the reference is free
    /// (expiring a stale claim lazily in the same session, ADR-009).
    /// </summary>
    private async Task<Result<IdempotencyDecision?>> ResolveExistingAsync(
        IDatabaseSession session, ChannelId channelId, TransactionIdentity identity, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var found = await _store.FindActiveAsync(session, channelId, identity.ClientReference, cancellationToken);
        if (found.IsFailure)
        {
            return found.Error;
        }

        if (found.Value is not { } record)
        {
            return Result<IdempotencyDecision?>.Success(null);
        }

        if (record.ExpiresAt <= now)
        {
            await _store.ExpireAsync(session, record.Id, now, cancellationToken);
            return Result<IdempotencyDecision?>.Success(null);
        }

        // Fingerprints are only comparable within one algorithm version (ADR-006). A version mismatch cannot prove
        // "same payload", so it is treated as a conflict: fail closed rather than process a possible duplicate.
        if (record.Fingerprint != identity.Fingerprint)
        {
            return RansysError.Conflict(
                ErrorCodes.DuplicateReferenceConflict,
                $"Client reference '{identity.ClientReference}' was already used with a different payload.");
        }

        return new IdempotencyDecision(IdempotencyOutcome.ExistingTransaction, record.TransactionId);
    }
}
