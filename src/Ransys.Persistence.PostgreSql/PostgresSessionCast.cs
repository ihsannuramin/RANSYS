using Ransys.Application;

namespace Ransys.Persistence.PostgreSql;

internal static class PostgresSessionCast
{
    /// <summary>Persistence adapters only work with their own session type.</summary>
    public static PostgresSession From(IDatabaseSession session) =>
        session as PostgresSession
        ?? throw new ArgumentException($"Expected a {nameof(PostgresSession)}.", nameof(session));
}
