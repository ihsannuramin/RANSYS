using Dapper;
using Npgsql;
using Ransys.Persistence.PostgreSql.Migrations;
using Xunit;

namespace Ransys.Testing.PostgreSql;

/// <summary>
/// Real PostgreSQL for database-semantics tests (main.md §21). On start it:
/// <list type="number">
/// <item>refuses any database whose name does not start with <c>RANSYS_PG</c> (protects other local databases);</item>
/// <item>takes a session-level advisory lock held for the whole test assembly, so test assemblies that run in
/// parallel never reset the schema under each other;</item>
/// <item>drops the Transaction DB schemas and the migration journal, then applies all migrations;</item>
/// <item>seeds baseline reference data.</item>
/// </list>
/// Use it through an xUnit collection fixture so tests in one assembly run sequentially.
/// </summary>
public sealed class PostgresDatabaseFixture : IAsyncLifetime
{
    public const string RequiredDatabasePrefix = "RANSYS_PG";

    private const long SuiteLockKey = 0x52414E5359535054; // "RANSYSPT"

    private static readonly string[] TransactionSchemas = ["core", "ledger", "integration", "config", "async"];

    private NpgsqlConnection? _lockConnection;
    private NpgsqlDataSource? _dataSource;
    private TestSeed? _seed;

    public string ConnectionString { get; } = TestDatabaseSettings.ConnectionString;

    public NpgsqlDataSource DataSource =>
        _dataSource ?? throw new InvalidOperationException("Fixture is not initialized.");

    public TestSeed Seed => _seed ?? throw new InvalidOperationException("Fixture is not initialized.");

    public IReadOnlyList<string> ScriptsAppliedAtStartup { get; private set; } = [];

    public async Task InitializeAsync()
    {
        var database = new NpgsqlConnectionStringBuilder(ConnectionString).Database;
        if (database is null || !database.StartsWith(RequiredDatabasePrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to reset database '{database}': test databases must start with '{RequiredDatabasePrefix}'.");
        }

        _lockConnection = new NpgsqlConnection(ConnectionString);
        try
        {
            await _lockConnection.OpenAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Cannot connect to the test PostgreSQL database '{database}'. Set {TestDatabaseSettings.ConnectionStringVariable}.", ex);
        }

        await _lockConnection.ExecuteAsync("SELECT pg_advisory_lock(@Key)", new { Key = SuiteLockKey }, commandTimeout: 0);

        var schemas = string.Join(", ", TransactionSchemas);
        await _lockConnection.ExecuteAsync(
            $"DROP SCHEMA IF EXISTS {schemas} CASCADE; DROP TABLE IF EXISTS {DatabaseMigrator.JournalSchema}.{DatabaseMigrator.JournalTable};");

        ScriptsAppliedAtStartup = DatabaseMigrator.Migrate(ConnectionString);

        _dataSource = NpgsqlDataSource.Create(ConnectionString);
        _seed = await TestSeed.CreateAsync(_dataSource);
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        if (_lockConnection is not null)
        {
            await _lockConnection.ExecuteAsync("SELECT pg_advisory_unlock(@Key)", new { Key = SuiteLockKey });
            await _lockConnection.DisposeAsync();
        }
    }
}
