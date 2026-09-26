using Ransys.Testing.PostgreSql;

namespace Ransys.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresDatabaseFixture>
{
    public const string Name = "postgres";
}
