using Ransys.Application;
using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Transactions;

namespace Ransys.TransactionCore;

/// <summary>A child transaction (REVERSAL, REFUND, VOID) with its principal amount (in the original's currency).</summary>
public sealed record ChildTransactionSummary(TransactionId Id, ProcessingStatus Status, decimal Amount);

/// <summary>Persistence port for the <see cref="Transaction"/> aggregate.</summary>
public interface ITransactionRepository
{
    Task<Result> InsertAsync(IDatabaseSession session, Transaction transaction, CancellationToken cancellationToken = default);

    /// <summary>Fails with CONCURRENCY_CONFLICT when the row changed since it was loaded (State Transition Matrix §57).</summary>
    Task<Result> UpdateAsync(IDatabaseSession session, Transaction transaction, CancellationToken cancellationToken = default);

    /// <summary>Child transactions (e.g. REVERSAL, REFUND) of <paramref name="originalId"/>, with their processing status.</summary>
    Task<IReadOnlyList<(TransactionId Id, ProcessingStatus Status)>> FindChildrenAsync(
        IDatabaseSession session, TransactionId originalId, TransactionType type, CancellationToken cancellationToken = default);

    /// <summary>
    /// Children of <paramref name="originalId"/> of one type with their amounts. Used for the cumulative refund cap and
    /// the refunded principal before a refund (ADR-023); callers hold the original's row lock.
    /// </summary>
    Task<IReadOnlyList<ChildTransactionSummary>> FindChildSummariesAsync(
        IDatabaseSession session, TransactionId originalId, TransactionType type, CancellationToken cancellationToken = default);

    /// <summary><paramref name="forUpdate"/> takes the row lock: step 1 of the finalization lock order.</summary>
    Task<Result<Transaction?>> GetAsync(
        IDatabaseSession session, TransactionId id, bool forUpdate, CancellationToken cancellationToken = default);
}
