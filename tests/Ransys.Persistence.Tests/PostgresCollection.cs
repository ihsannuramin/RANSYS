using Ransys.Testing.PostgreSql;

namespace Ransys.Persistence.Tests;

/// <summary>All database tests in this assembly share one migrated database and run sequentially.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresDatabaseFixture>
{
    public const string Name = "postgres";
}
