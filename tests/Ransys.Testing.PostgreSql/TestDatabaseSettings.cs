namespace Ransys.Testing.PostgreSql;

/// <summary>
/// Resolves the PostgreSQL connection used by database-backed tests.
/// Tests run against a real PostgreSQL instance (never an in-memory substitute),
/// because they depend on row locking, unique indexes, and deferred triggers.
/// </summary>
public static class TestDatabaseSettings
{
    public const string ConnectionStringVariable = "RANSYS_TEST_PG";

    public const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=RANSYS_PG;Username=postgres";

    public static string ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionStringVariable) is { Length: > 0 } value
            ? value
            : DefaultConnectionString;
}
