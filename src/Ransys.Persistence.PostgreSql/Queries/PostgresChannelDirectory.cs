using Dapper;
using Ransys.Application;
using Ransys.Domain;
using Ransys.TransactionCore.Processing;

namespace Ransys.Persistence.PostgreSql.Queries;

/// <summary>Reads (in its own read-only session) <c>core.channels</c> joined with <c>core.merchants</c>; only ACTIVE channels of ACTIVE merchants resolve.</summary>
public sealed class PostgresChannelDirectory(IDatabaseSessionFactory sessions) : IChannelDirectory
{
    static PostgresChannelDirectory() => DbValues.EnsureConfigured();

    public async Task<ChannelIdentity?> FindActiveChannelAsync(ChannelId channelId, CancellationToken cancellationToken = default)
    {
        await using var session = await sessions.BeginAsync(cancellationToken);
        var s = PostgresSessionCast.From(session);
        var merchant = await s.Connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            """
            SELECT c.merchant_id
            FROM core.channels c
            JOIN core.merchants m ON m.merchant_id = c.merchant_id
            WHERE c.channel_id = @Id AND c.status = 'ACTIVE' AND m.status = 'ACTIVE'
            """,
            new { Id = channelId.Value }, s.Transaction, cancellationToken: cancellationToken));
        return merchant is { } id ? new ChannelIdentity(channelId, new MerchantId(id)) : null;
    }
}
