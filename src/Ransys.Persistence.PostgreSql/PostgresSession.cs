using System.Data;
using Npgsql;
using Ransys.Application;

namespace Ransys.Persistence.PostgreSql;

/// <summary>
/// One explicit PostgreSQL connection + transaction. Every store call made with the same session is part of
/// the same atomic unit (ERD v1.1 §43: transaction, wallet, reservation, journal, history and outbox commit
/// together or not at all). Disposing without <see cref="CommitAsync"/> rolls back.
/// <para>
/// If a session is rolled back, discard any aggregate instances that were saved through it and reload them:
/// stores advance the aggregate's row version optimistically.
/// </para>
/// </summary>
public sealed class PostgresSession : IDatabaseSession, IAsyncDisposable
{
    private bool _completed;

    private PostgresSession(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        Connection = connection;
        Transaction = transaction;
    }

    public NpgsqlConnection Connection { get; }

    public NpgsqlTransaction Transaction { get; }

    public static async Task<PostgresSession> BeginAsync(
        NpgsqlDataSource dataSource,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
            return new PostgresSession(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        await Transaction.CommitAsync(cancellationToken);
        _completed = true;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        await Transaction.RollbackAsync(cancellationToken);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_completed && Transaction.Connection is not null)
            {
                await Transaction.RollbackAsync();
            }
        }
        finally
        {
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private void EnsureActive()
    {
        if (_completed)
        {
            throw new InvalidOperationException("The session has already been committed or rolled back.");
        }
    }
}
