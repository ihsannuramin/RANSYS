using Dapper;
using Ransys.Domain.Routing;
using Ransys.Persistence.PostgreSql.Migrations;
using Ransys.Testing.PostgreSql;

namespace Ransys.Persistence.Tests;

[Collection(PostgresCollection.Name)]
public sealed class MigrationTests(PostgresDatabaseFixture db)
{
    [Fact]
    public void All_scripts_were_applied_on_a_fresh_database_in_order()
    {
        Assert.Equal(DatabaseMigrator.ScriptNames, db.ScriptsAppliedAtStartup);
        Assert.Equal(7, DatabaseMigrator.ScriptNames.Count);
    }

    [Fact]
    public void Running_migrations_again_is_a_no_op()
    {
        Assert.Empty(DatabaseMigrator.Migrate(db.ConnectionString));
    }

    [Fact]
    public void Baseline_script_is_the_reference_ddl_unchanged()
    {
        var root = RepositoryRoot();
        var reference = File.ReadAllText(Path.Combine(root, "docs", "RANSYS_PostgreSQL_Reference_DDL_v1.1.sql"));
        var baseline = File.ReadAllText(Path.Combine(
            root, "src", "Ransys.Persistence.PostgreSql", "Migrations", "Scripts", "0001_baseline_reference_ddl_v1_1.sql"));

        Assert.Equal(Normalize(reference), Normalize(baseline));
    }

    [Theory]
    [InlineData("core", "merchants")]
    [InlineData("core", "channels")]
    [InlineData("core", "products")]
    [InlineData("core", "currency_definitions")]
    [InlineData("core", "transactions")]
    [InlineData("core", "transaction_fee_components")]
    [InlineData("core", "idempotency_records")]
    [InlineData("core", "transaction_attempts")]
    [InlineData("core", "transaction_state_history")]
    [InlineData("core", "topup_requests")]
    [InlineData("ledger", "wallets")]
    [InlineData("ledger", "balance_reservations")]
    [InlineData("ledger", "ledger_accounts")]
    [InlineData("ledger", "ledger_transactions")]
    [InlineData("ledger", "ledger_entries")]
    [InlineData("integration", "providers")]
    [InlineData("config", "config_versions")]
    [InlineData("async", "outbox_events")]
    [InlineData("async", "outbox_delivery_attempts")]
    public async Task Phase1_tables_exist(string schema, string table)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();

        var exists = await connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = @schema AND table_name = @table)",
            new { schema, table });

        Assert.True(exists, $"{schema}.{table} is missing");
    }

    [Theory]
    [InlineData("transaction_state_history", "status_dimension")] // ADR-004
    [InlineData("transactions", "routing_rule_version")]         // ADR-013
    [InlineData("transactions", "failover_count")]
    [InlineData("transactions", "failover_reason")]
    [InlineData("transactions", "routing_decided_at")]
    [InlineData("transactions", "canonical_detail")]
    [InlineData("transaction_attempts", "outcome_recorded_at")] // ADR-005
    [InlineData("transaction_fee_components", "refund_policy")] // ADR-014
    public async Task Expand_migration_columns_exist(string table, string column)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();

        var exists = await connection.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (SELECT 1 FROM information_schema.columns
                           WHERE table_schema = 'core' AND table_name = @table AND column_name = @column)
            """,
            new { table, column });

        Assert.True(exists, $"core.{table}.{column} is missing");
    }

    /// <summary>ADR-018: migration 0007 expands the transport status CHECK with PROTOCOL_ERROR and keeps the old values.</summary>
    [Fact]
    public async Task Transport_status_check_accepts_protocol_error()
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();

        var definition = await connection.ExecuteScalarAsync<string>(
            "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'ck_attempt_transport_status'");

        Assert.NotNull(definition);
        foreach (var code in new[] { "NOT_SENT", "SENT", "RESPONSE", "TIMEOUT", "CONNECTION_ERROR", "PROTOCOL_ERROR" })
        {
            Assert.Contains($"'{code}'", definition, StringComparison.Ordinal);
        }
    }

    /// <summary>ADR-018: the 0007 rename maps every legacy <c>supports_*</c> code, and a duplicate spelling keeps the new row.</summary>
    [Fact]
    public async Task Capability_rename_maps_legacy_codes_to_the_adapter_catalog()
    {
        var script = ReadScript("0007_capability_catalog_and_protocol_error.sql");
        var dataPart = script[..script.IndexOf("ALTER TABLE", StringComparison.Ordinal)];
        string[] legacy =
        [
            "inquiry", "payment", "purchase", "transfer", "void", "status_check", "reversal", "refund", "advice",
            "callback", "balance_check", "reconciliation", "settlement_file",
        ];

        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var provider = db.Seed.ProviderBId;
        await connection.ExecuteAsync(
            "DELETE FROM integration.provider_capabilities WHERE provider_id = @provider", new { provider }, transaction);
        foreach (var name in legacy)
        {
            await connection.ExecuteAsync(
                "INSERT INTO integration.provider_capabilities VALUES (@provider, @code, true, '{}'::jsonb, now())",
                new { provider, code = "supports_" + name },
                transaction);
        }

        // The new spelling already exists (disabled) for PAYMENT: it must win, and stay disabled (fail closed).
        await connection.ExecuteAsync(
            "INSERT INTO integration.provider_capabilities VALUES (@provider, 'PAYMENT', false, '{}'::jsonb, now())",
            new { provider },
            transaction);

        await connection.ExecuteAsync(dataPart, transaction: transaction);

        var rows = (await connection.QueryAsync<(string Code, bool Enabled)>(
            "SELECT capability_code, enabled FROM integration.provider_capabilities WHERE provider_id = @provider",
            new { provider },
            transaction)).ToDictionary(r => r.Code, r => r.Enabled);

        Assert.Equal(ProviderCapabilities.All.ToHashSet(), rows.Keys.ToHashSet());
        Assert.False(rows[ProviderCapabilities.Payment]);
        Assert.True(rows[ProviderCapabilities.Void]);

        await transaction.RollbackAsync();
    }

    private static string ReadScript(string name) => File.ReadAllText(Path.Combine(
        RepositoryRoot(), "src", "Ransys.Persistence.PostgreSql", "Migrations", "Scripts", name));

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ransys.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Ransys.sln not found.");
    }
}
