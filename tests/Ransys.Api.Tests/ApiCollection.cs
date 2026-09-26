using Ransys.Testing.PostgreSql;

namespace Ransys.Api.Tests;

/// <summary>
/// One collection for every test of this assembly: <see cref="PostgresDatabaseFixture"/> resets the schemas, so API tests
/// run sequentially against one database and share one in-process API host (<see cref="ApiFactory"/>).
/// </summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<PostgresDatabaseFixture>, ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
