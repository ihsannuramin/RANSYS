using Npgsql;
using Ransys.Application;

namespace Ransys.Persistence.PostgreSql;

public sealed class PostgresSessionFactory(NpgsqlDataSource dataSource) : IDatabaseSessionFactory
{
    public async Task<IDatabaseSession> BeginAsync(CancellationToken cancellationToken = default) =>
        await PostgresSession.BeginAsync(dataSource, cancellationToken: cancellationToken);
}
