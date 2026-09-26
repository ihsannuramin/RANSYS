using Dapper;
using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Ledger;
using Ransys.Domain.Monetary;
using Ransys.Infrastructure;
using Ransys.Persistence.PostgreSql;
using Ransys.Persistence.PostgreSql.Ledger;
using Ransys.Persistence.PostgreSql.Outbox;
using Ransys.Persistence.PostgreSql.ReferenceData;
using Ransys.Testing.PostgreSql;

namespace Ransys.Ledger.Tests;

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresDatabaseFixture>
{
    public const string Name = "postgres";
}

/// <summary>Test harness around the real Ledger Posting Service and PostgreSQL stores.</summary>
internal sealed class LedgerHarness(PostgresDatabaseFixture db)
{
    public static readonly CurrencyDefinition Idr = CurrencyDefinition.Create("IDR", 1, 2).Value;

    public LedgerPostingService Service { get; } = new(
        new PostgresLedgerStore(new ReferenceDataStore()), new PostgresOutboxWriter(), new SystemClock(), new UuidV7IdGenerator());

    public ProviderId ProviderA => new(db.Seed.ProviderAId);

    public static Money Rp(decimal amount) => Money.Create(amount, Idr).Value;

    /// <summary>Runs <paramref name="work"/> in its own session and commits only when it succeeds.</summary>
    public async Task<Result<T>> InSession<T>(Func<PostgresSession, Task<Result<T>>> work)
    {
        await using var session = await PostgresSession.BeginAsync(db.DataSource);
        var result = await work(session);
        if (result.IsSuccess)
        {
            await session.CommitAsync();
        }

        return result;
    }

    public Task<Result<LedgerPostingResult>> Reserve(TransactionId tx, WalletId wallet, decimal principal, decimal fee = 0m) =>
        InSession(s => Service.ReserveAsync(s, new ReserveRequest(tx, wallet, Rp(principal), Rp(fee), "PAYMENT_PROCESSING")));

    public Task<Result<LedgerPostingResult>> Post(TransactionId tx, PaymentSplit? split = null) =>
        InSession(s => Service.PostPaymentAsync(s, new PostPaymentRequest(tx, ProviderA, split)));

    public Task<Result<LedgerPostingResult>> Release(TransactionId tx, bool asReversal = false) =>
        InSession(s => Service.ReleaseReservationAsync(s, new ReleaseRequest(tx, asReversal)));

    public Task<Result<LedgerPostingResult>> TopUp(WalletId wallet, decimal amount, string? reference = null) =>
        InSession(s => Service.PostTopUpAsync(
            s, new TopUpRequest(wallet, reference ?? $"TP-{Guid.NewGuid():N}", Rp(amount), null, LedgerActor.System)));

    /// <summary>New merchant with an empty main wallet, then funded through a real top-up posting.</summary>
    public async Task<WalletId> NewFundedWallet(decimal balance)
    {
        var merchant = Guid.CreateVersion7();
        var wallet = new WalletId(Guid.CreateVersion7());
        await using (var connection = await db.DataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO core.merchants VALUES (@merchant, @code, 'Ledger Test Merchant', 'ACTIVE', now(), now());
                INSERT INTO ledger.wallets
                    (wallet_id, merchant_id, product_id, currency_definition_id, ledger_balance, available_balance,
                     reserved_balance, status, created_at, updated_at)
                VALUES (@wallet, @merchant, NULL, @currency, 0, 0, 0, 'ACTIVE', now(), now());
                """,
                new { merchant, code = $"MRC-{merchant:N}", wallet = wallet.Value, currency = db.Seed.IdrV1CurrencyDefinitionId });
        }

        if (balance > 0m)
        {
            var topUp = await TopUp(wallet, balance);
            Assert.True(topUp.IsSuccess, topUp.IsFailure ? topUp.Error.ToString() : null);
        }

        return wallet;
    }

    /// <summary>A bare <c>core.transactions</c> row so reservations and journals can reference it.</summary>
    public async Task<TransactionId> NewTransaction()
    {
        var id = Guid.CreateVersion7();
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO core.transactions
                (ransys_transaction_id, merchant_id, channel_id, product_id, transaction_type, client_reference,
                 transaction_fingerprint, amount, currency_definition_id, processing_status, financial_status,
                 reconciliation_status, settlement_status, received_at, updated_at)
            VALUES (@id, @merchant, @channel, @product, 'PAYMENT', @reference, 'v1:test', 0, @currency,
                    'VALIDATED', 'NONE', 'UNMATCHED', 'NOT_APPLICABLE', now(), now())
            """,
            new
            {
                id,
                merchant = db.Seed.MerchantId,
                channel = db.Seed.ChannelId,
                product = db.Seed.ProductId,
                reference = $"INV-{id:N}",
                currency = db.Seed.IdrV1CurrencyDefinitionId,
            });
        return new TransactionId(id);
    }

    public async Task<(decimal Ledger, decimal Available, decimal Reserved)> Balances(WalletId wallet)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<(decimal, decimal, decimal)>(
            "SELECT ledger_balance, available_balance, reserved_balance FROM ledger.wallets WHERE wallet_id = @id",
            new { id = wallet.Value });
    }

    /// <summary>
    /// Wallet projection reconstructed from immutable ledger entries (Ledger Posting Rule Matrix §8, §55):
    /// merchant accounts are liabilities, so balance = credits − debits.
    /// </summary>
    public async Task<(decimal Available, decimal Reserved)> BalancesFromLedger(WalletId wallet)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<(decimal, decimal)>(
            """
            SELECT
              COALESCE(SUM(CASE WHEN a.account_code = @available THEN (CASE e.entry_side WHEN 'C' THEN e.amount ELSE -e.amount END) END), 0),
              COALESCE(SUM(CASE WHEN a.account_code = @reserved THEN (CASE e.entry_side WHEN 'C' THEN e.amount ELSE -e.amount END) END), 0)
            FROM ledger.ledger_entries e
            JOIN ledger.ledger_accounts a ON a.ledger_account_id = e.ledger_account_id
            WHERE a.wallet_id = @wallet
            """,
            new { wallet = wallet.Value, available = $"MERCHANT:{wallet}:AVAILABLE", reserved = $"MERCHANT:{wallet}:RESERVED" });
    }

    public async Task<int> JournalCount(string postingKeyPrefix)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM ledger.ledger_transactions WHERE starts_with(posting_key, @prefix)", new { prefix = postingKeyPrefix });
    }

    public async Task<List<(string Side, decimal Amount, string Code)>> Entries(string postingKey)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        var rows = await connection.QueryAsync<(string, decimal, string)>(
            """
            SELECT e.entry_side, e.amount, a.account_code
            FROM ledger.ledger_transactions t
            JOIN ledger.ledger_entries e ON e.ledger_transaction_id = t.ledger_transaction_id
            JOIN ledger.ledger_accounts a ON a.ledger_account_id = e.ledger_account_id
            WHERE t.posting_key = @postingKey
            ORDER BY e.entry_sequence
            """,
            new { postingKey });
        return rows.ToList();
    }

    public async Task<(string Status, string HoldReason)?> ReservationState(TransactionId tx)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<(string, string)?>(
            "SELECT status, hold_reason FROM ledger.balance_reservations WHERE ransys_transaction_id = @id", new { id = tx.Value });
    }

    public async Task<List<string>> OutboxEventTypes(WalletId wallet)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        var rows = await connection.QueryAsync<string>(
            "SELECT event_type FROM async.outbox_events WHERE aggregate_id = @id ORDER BY created_at, event_id", new { id = wallet.Value });
        return rows.ToList();
    }
}
