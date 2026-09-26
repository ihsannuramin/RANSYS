using Ransys.Application;
using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Transactions;

namespace Ransys.TransactionCore;

/// <summary>Persistence port for the <see cref="Transaction"/> aggregate.</summary>
public interface ITransactionRepository
{
    Task<Result> InsertAsync(IDatabaseSession session, Transaction transaction, CancellationToken cancellationToken = default);

    /// <summary>Fails with CONCURRENCY_CONFLICT when the row changed since it was loaded (State Transition Matrix §57).</summary>
    Task<Result> UpdateAsync(IDatabaseSession session, Transaction transaction, CancellationToken cancellationToken = default);

    /// <summary><paramref name="forUpdate"/> takes the row lock: step 1 of the finalization lock order.</summary>
    Task<Result<Transaction?>> GetAsync(
        IDatabaseSession session, TransactionId id, bool forUpdate, CancellationToken cancellationToken = default);
}
