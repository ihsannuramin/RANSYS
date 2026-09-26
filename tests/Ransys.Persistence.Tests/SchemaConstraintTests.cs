using Dapper;
using Npgsql;
using Ransys.Testing.PostgreSql;

namespace Ransys.Persistence.Tests;

/// <summary>
/// The database is the last line of defense (ERD v1.1 §20–21, §42). These tests prove the reference DDL
/// constraints and triggers behave as specified on a real PostgreSQL server.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SchemaConstraintTests(PostgresDatabaseFixture db)
{
    private const string CheckViolation = "23514";
    private const string UniqueViolation = "23505";
    private const string RaiseException = "P0001";

    [Fact]
    public async Task Wallet_balances_cannot_be_negative()
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertWallet(connection, available: -0.01m));

        Assert.Equal(CheckViolation, ex.SqlState);
        Assert.Equal("ck_wallet_available_nonnegative", ex.ConstraintName);
    }

    [Fact]
    public async Task Balanced_journal_commits()
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        var (debit, credit) = await CreateAccounts(connection);
        await using var tx = await connection.BeginTransactionAsync();

        var journal = await InsertJournal(connection, tx, $"TEST:BALANCED:{Guid.NewGuid()}");
        await InsertEntry(connection, tx, journal, debit, 1, "D", 100m);
        await InsertEntry(connection, tx, journal, credit, 2, "C", 100m);

        await tx.CommitAsync();
    }

    [Fact]
    public async Task Unbalanced_posted_journal_is_rejected_at_commit()
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        var (debit, credit) = await CreateAccounts(connection);
        await using var tx = await connection.BeginTransactionAsync();

        var journal = await InsertJournal(connection, tx, $"TEST:UNBALANCED:{Guid.NewGuid()}");
        await InsertEntry(connection, tx, journal, debit, 1, "D", 100m);
        await InsertEntry(connection, tx, journal, credit, 2, "C", 99.99m);

        // Deferred constraint trigger: the violation surfaces at COMMIT.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
        Assert.Equal(RaiseException, ex.SqlState);
    }

    [Fact]
    public async Task Single_entry_posted_journal_is_rejected()
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        var (debit, _) = await CreateAccounts(connection);
        await using var tx = await connection.BeginTransactionAsync();

        var journal = await InsertJournal(connection, tx, $"TEST:SINGLE:{Guid.NewGuid()}");
        await InsertEntry(connection, tx, journal, debit, 1, "D", 100m);

        await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
    }

    [Fact]
    public async Task Posted_entries_are_immutable()
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        var (debit, credit) = await CreateAccounts(connection);
        Guid journal;
        await using (var tx = await connection.BeginTransactionAsync())
        {
            journal = await InsertJournal(connection, tx, $"TEST:IMMUTABLE:{Guid.NewGuid()}");
            await InsertEntry(connection, tx, journal, debit, 1, "D", 50m);
            await InsertEntry(connection, tx, journal, credit, 2, "C", 50m);
            await tx.CommitAsync();
        }

        var update = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
            "UPDATE ledger.ledger_entries SET amount = 1 WHERE ledger_transaction_id = @journal", new { journal }));
        var delete = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
            "DELETE FROM ledger.ledger_entries WHERE ledger_transaction_id = @journal", new { journal }));

        Assert.Equal(RaiseException, update.SqlState);
        Assert.Equal(RaiseException, delete.SqlState);
    }

    [Fact]
    public async Task Posting_key_is_unique()
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        var (debit, credit) = await CreateAccounts(connection);
        var key = $"TX:{Guid.NewGuid()}:POST";

        await using (var tx = await connection.BeginTransactionAsync())
        {
            var journal = await InsertJournal(connection, tx, key);
            await InsertEntry(connection, tx, journal, debit, 1, "D", 10m);
            await InsertEntry(connection, tx, journal, credit, 2, "C", 10m);
            await tx.CommitAsync();
        }

        await using var retry = await connection.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertJournal(connection, retry, key));
        Assert.Equal(UniqueViolation, ex.SqlState);
    }

    [Fact]
    public async Task Only_one_active_idempotency_record_per_channel_and_reference()
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        var reference = $"INV-{Guid.NewGuid():N}";
        var tx1 = await InsertBareTransaction(connection, reference);
        var tx2 = await InsertBareTransaction(connection, reference);

        await InsertIdempotency(connection, reference, tx1, active: true);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertIdempotency(connection, reference, tx2, active: true));
        Assert.Equal(UniqueViolation, ex.SqlState);

        // Partial unique index: an expired (inactive) record does not block a new active one.
        await connection.ExecuteAsync(
            "UPDATE core.idempotency_records SET active = false, expired_at = now() WHERE ransys_transaction_id = @tx1", new { tx1 });
        await InsertIdempotency(connection, reference, tx2, active: true);
    }

    [Fact]
    public async Task Reservation_total_must_equal_principal_plus_fee()
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        var wallet = await InsertWallet(connection, available: 0m);
        var transaction = await InsertBareTransaction(connection, $"INV-{Guid.NewGuid():N}");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
            """
            INSERT INTO ledger.balance_reservations
                (reservation_id, wallet_id, ransys_transaction_id, principal_amount, fee_amount,
                 total_reserved_amount, status, hold_reason, created_at)
            VALUES (@id, @wallet, @transaction, 100000, 2500, 100000, 'ACTIVE', 'PAYMENT_PROCESSING', now())
            """,
            new { id = Guid.CreateVersion7(), wallet, transaction }));

        Assert.Equal("ck_reservation_total", ex.ConstraintName);
    }

    [Fact]
    public async Task Unknown_processing_status_is_rejected()
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertBareTransaction(connection, $"INV-{Guid.NewGuid():N}", processingStatus: "RECON_EXCEPTION"));

        Assert.Equal("ck_tx_processing_status", ex.ConstraintName);
    }

    private async Task<Guid> InsertWallet(NpgsqlConnection connection, decimal available)
    {
        // Each wallet gets its own merchant: main wallets are unique per merchant and currency.
        var merchant = Guid.CreateVersion7();
        var wallet = Guid.CreateVersion7();
        await connection.ExecuteAsync(
            """
            INSERT INTO core.merchants VALUES (@merchant, @code, 'Wallet Test Merchant', 'ACTIVE', now(), now());
            INSERT INTO ledger.wallets
                (wallet_id, merchant_id, product_id, currency_definition_id, ledger_balance, available_balance,
                 reserved_balance, status, created_at, updated_at)
            VALUES (@wallet, @merchant, NULL, @currency, 0, @available, 0, 'ACTIVE', now(), now());
            """,
            new { merchant, code = $"MRC-{merchant:N}", wallet, currency = db.Seed.IdrV1CurrencyDefinitionId, available });
        return wallet;
    }

    private async Task<(Guid Debit, Guid Credit)> CreateAccounts(NpgsqlConnection connection)
    {
        var debit = Guid.CreateVersion7();
        var credit = Guid.CreateVersion7();
        await connection.ExecuteAsync(
            """
            INSERT INTO ledger.ledger_accounts
                (ledger_account_id, account_code, account_name, owner_type, account_class, account_type,
                 currency_definition_id, normal_side, status, created_at)
            VALUES (@debit, @debitCode, 'Test Debit', 'SYSTEM', 'ASSET', 'CASH_CLEARING', @currency, 'D', 'ACTIVE', now()),
                   (@credit, @creditCode, 'Test Credit', 'SYSTEM', 'LIABILITY', 'MERCHANT_AVAILABLE', @currency, 'C', 'ACTIVE', now());
            """,
            new { debit, debitCode = $"TEST:D:{debit}", credit, creditCode = $"TEST:C:{credit}", currency = db.Seed.IdrV1CurrencyDefinitionId });
        return (debit, credit);
    }

    private static async Task<Guid> InsertJournal(NpgsqlConnection connection, NpgsqlTransaction tx, string postingKey)
    {
        var id = Guid.CreateVersion7();
        await connection.ExecuteAsync(
            """
            INSERT INTO ledger.ledger_transactions
                (ledger_transaction_id, posting_key, operation_type, business_reference, description, status,
                 effective_at, created_at, created_by_type)
            VALUES (@id, @postingKey, 'TOPUP', 'TEST', 'constraint test', 'POSTED', now(), now(), 'SYSTEM')
            """,
            new { id, postingKey },
            tx);
        return id;
    }

    private async Task InsertEntry(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid journal, Guid account, short sequence, string side, decimal amount) =>
        await connection.ExecuteAsync(
            """
            INSERT INTO ledger.ledger_entries
                (ledger_entry_id, ledger_transaction_id, ledger_account_id, entry_sequence, entry_side, amount,
                 currency_definition_id, created_at)
            VALUES (@id, @journal, @account, @sequence, @side, @amount, @currency, now())
            """,
            new { id = Guid.CreateVersion7(), journal, account, sequence, side, amount, currency = db.Seed.IdrV1CurrencyDefinitionId },
            tx);

    private async Task<Guid> InsertBareTransaction(NpgsqlConnection connection, string clientReference, string processingStatus = "RECEIVED")
    {
        var id = Guid.CreateVersion7();
        await connection.ExecuteAsync(
            """
            INSERT INTO core.transactions
                (ransys_transaction_id, merchant_id, channel_id, product_id, transaction_type, client_reference,
                 transaction_fingerprint, amount, currency_definition_id, processing_status, financial_status,
                 reconciliation_status, settlement_status, received_at, updated_at)
            VALUES (@id, @merchant, @channel, @product, 'PAYMENT', @clientReference, 'v1:test', 100000, @currency,
                    @processingStatus, 'NONE', 'UNMATCHED', 'NOT_APPLICABLE', now(), now())
            """,
            new
            {
                id,
                merchant = db.Seed.MerchantId,
                channel = db.Seed.ChannelId,
                product = db.Seed.ProductId,
                clientReference,
                currency = db.Seed.IdrV1CurrencyDefinitionId,
                processingStatus,
            });
        return id;
    }

    private async Task InsertIdempotency(NpgsqlConnection connection, string reference, Guid transaction, bool active) =>
        await connection.ExecuteAsync(
            """
            INSERT INTO core.idempotency_records
                (idempotency_record_id, channel_id, client_reference, fingerprint, ransys_transaction_id, active,
                 created_at, expires_at)
            VALUES (@id, @channel, @reference, 'v1:test', @transaction, @active, now(), now() + interval '24 hours')
            """,
            new { id = Guid.CreateVersion7(), channel = db.Seed.ChannelId, reference, transaction, active });
}
