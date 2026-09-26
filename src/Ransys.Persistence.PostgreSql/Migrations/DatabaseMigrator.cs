using System.Reflection;
using DbUp;
using DbUp.Engine;

namespace Ransys.Persistence.PostgreSql.Migrations;

/// <summary>
/// Applies the Transaction Database schema from embedded, versioned SQL scripts (ADR-011).
/// Scripts are immutable once released; changes follow expand → migrate → contract (ERD v1.1 §45).
/// The Backoffice Database has its own migrations (DDL v1.1 note) and is not handled here.
/// </summary>
public static class DatabaseMigrator
{
    public const string JournalSchema = "public";
    public const string JournalTable = "ransys_schema_versions";

    private const string ScriptNamespace = "Ransys.Persistence.PostgreSql.Migrations.Scripts.";

    /// <summary>Names of all embedded scripts, in execution order.</summary>
    public static IReadOnlyList<string> ScriptNames { get; } = typeof(DatabaseMigrator).Assembly
        .GetManifestResourceNames()
        .Where(name => name.StartsWith(ScriptNamespace, StringComparison.Ordinal))
        .Order(StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// Runs pending scripts, each in its own transaction. Returns the scripts executed by this call.
    /// Throws when a script fails: the application must not start against a partially migrated schema.
    /// </summary>
    public static IReadOnlyList<string> Migrate(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                Assembly.GetExecutingAssembly(),
                name => name.StartsWith(ScriptNamespace, StringComparison.Ordinal))
            .WithTransactionPerScript()
            .WithVariablesDisabled()
            .JournalToPostgresqlTable(JournalSchema, JournalTable)
            .LogToNowhere()
            .Build();

        DatabaseUpgradeResult result = upgrader.PerformUpgrade();
        if (!result.Successful)
        {
            throw new InvalidOperationException(
                $"Database migration failed at script '{result.ErrorScript?.Name}'.", result.Error);
        }

        return result.Scripts.Select(s => s.Name).ToList();
    }
}
