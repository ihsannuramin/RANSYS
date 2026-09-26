using Dapper;
using Ransys.Application;
using Ransys.Configuration;

namespace Ransys.Persistence.PostgreSql.Configuration;

public sealed class PostgresConfigVersionStore : IConfigVersionStore
{
    static PostgresConfigVersionStore() => DbValues.EnsureConfigured();

    public async Task<ConfigVersion?> GetActiveAsync(
        IDatabaseSession session, string domain, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        var s = PostgresSessionCast.From(session);
        var row = await s.Connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            """
            SELECT config_version_id, config_domain, version_no, effective_from, effective_until
            FROM config.config_versions
            WHERE config_domain = @Domain
              AND status = 'ACTIVE'
              AND (effective_from IS NULL OR effective_from <= @At)
              AND (effective_until IS NULL OR effective_until > @At)
            ORDER BY version_no DESC
            LIMIT 1
            """,
            new { Domain = domain, At = DbValues.ToDb(at) }, s.Transaction, cancellationToken: cancellationToken));

        return row is null
            ? null
            : new ConfigVersion(row.ConfigVersionId, row.ConfigDomain, row.VersionNo, DbValues.FromDb(row.EffectiveFrom), DbValues.FromDb(row.EffectiveUntil));
    }

    private sealed class Row
    {
        public Guid ConfigVersionId { get; init; }

        public string ConfigDomain { get; init; } = "";

        public long VersionNo { get; init; }

        public DateTime? EffectiveFrom { get; init; }

        public DateTime? EffectiveUntil { get; init; }
    }
}
