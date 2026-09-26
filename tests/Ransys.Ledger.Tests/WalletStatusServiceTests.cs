using Dapper;
using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Ledger;
using Ransys.Infrastructure;
using Ransys.Persistence.PostgreSql;
using Ransys.Persistence.PostgreSql.Ledger;
using Ransys.Persistence.PostgreSql.Outbox;
using Ransys.Persistence.PostgreSql.ReferenceData;
using Ransys.Testing.PostgreSql;
using static Ransys.Ledger.Tests.LedgerHarness;

namespace Ransys.Ledger.Tests;

/// <summary>ADR-016 wallet status operations on a real PostgreSQL server.</summary>
[Collection(PostgresCollection.Name)]
public sealed class WalletStatusServiceTests(PostgresDatabaseFixture db)
{
    private readonly LedgerHarness _h = new(db);

    private readonly WalletStatusService _status = new(
        new PostgresLedgerStore(new ReferenceDataStore()), new PostgresOutboxWriter(), new SystemClock(), new UuidV7IdGenerator());

    [Fact]
    public async Task Frozen_wallet_rejects_new_reservations_but_finalizes_existing_ones()
    {
        var wallet = await _h.NewFundedWallet(500_000m);
        var inFlight = await _h.NewTransaction();
        var toRelease = await _h.NewTransaction();
        await _h.Reserve(inFlight, wallet, 100_000m, fee: 2_500m);
        await _h.Reserve(toRelease, wallet, 50_000m);

        Assert.Equal(WalletStatus.Frozen, (await Change(wallet, WalletStatusCommand.Freeze)).Value.NewStatus);

        Assert.Equal(ErrorCodes.WalletNotActive, (await _h.Reserve(await _h.NewTransaction(), wallet, 1m)).Error.Code);
        Assert.True((await _h.Post(inFlight)).IsSuccess);
        Assert.True((await _h.Release(toRelease)).IsSuccess);
        Assert.True((await _h.InSession(s => _h.Service.PostRefundAsync(s, new RefundRequest(
            inFlight, null, "RF-FROZEN", _h.ProviderA, Rp(10_000m), Rp(0m), Guid.CreateVersion7(), LedgerActor.System)))).IsSuccess);
        Assert.True((await _h.TopUp(wallet, 1_000m)).IsSuccess);
        Assert.Equal("FROZEN", await StatusOf(wallet));
    }

    [Fact]
    public async Task Unfreeze_allows_reservations_again()
    {
        var wallet = await _h.NewFundedWallet(100_000m);
        await Change(wallet, WalletStatusCommand.Freeze);

        var unfrozen = await Change(wallet, WalletStatusCommand.Unfreeze);

        Assert.Equal((WalletStatus.Frozen, WalletStatus.Active, true),
            (unfrozen.Value.PreviousStatus, unfrozen.Value.NewStatus, unfrozen.Value.Changed));
        Assert.True((await _h.Reserve(await _h.NewTransaction(), wallet, 1_000m)).IsSuccess);
    }

    [Fact]
    public async Task Close_is_rejected_while_balance_or_unresolved_transactions_remain()
    {
        var withBalance = await _h.NewFundedWallet(1_000m);
        Assert.Equal(ErrorCodes.WalletNotEmpty, (await Change(withBalance, WalletStatusCommand.Close)).Error.Code);

        // Balance is zero only because everything is reserved: an ACTIVE reservation blocks closing too.
        var reservedOnly = await _h.NewFundedWallet(1_000m);
        await _h.Reserve(await _h.NewTransaction(), reservedOnly, 1_000m);
        await Drain(reservedOnly, 0m);
        Assert.Equal(ErrorCodes.WalletNotEmpty, (await Change(reservedOnly, WalletStatusCommand.Close)).Error.Code);

        // Posted but the transaction is still open (e.g. not yet finalized in core): unresolved.
        var openTransaction = await _h.NewFundedWallet(1_000m);
        await _h.Reserve(await _h.NewTransaction(), openTransaction, 1_000m);
        var tx = await OnlyTransactionOf(openTransaction);
        await _h.Post(tx);
        Assert.Equal(ErrorCodes.WalletHasUnresolvedTransactions, (await Change(openTransaction, WalletStatusCommand.Close)).Error.Code);
    }

    [Fact]
    public async Task Emptied_wallet_without_open_transactions_closes_and_becomes_terminal()
    {
        var wallet = await _h.NewFundedWallet(1_000m);
        var tx = await _h.NewTransaction();
        await _h.Reserve(tx, wallet, 400m);
        await _h.Post(tx);
        await MarkResolved(tx);
        await Drain(wallet, 600m);

        var closed = await Change(wallet, WalletStatusCommand.Close);

        Assert.Equal(WalletStatus.Closed, closed.Value.NewStatus);
        Assert.Equal("CLOSED", await StatusOf(wallet));
        Assert.Equal(ErrorCodes.WalletClosed, (await _h.TopUp(wallet, 1m)).Error.Code);
        Assert.Equal(ErrorCodes.WalletClosed, (await Change(wallet, WalletStatusCommand.Unfreeze)).Error.Code);
        Assert.False((await Change(wallet, WalletStatusCommand.Close)).Value.Changed);
    }

    [Fact]
    public async Task Status_change_emits_an_audit_event_with_actor_and_reason()
    {
        var wallet = await _h.NewFundedWallet(1_000m);
        var officer = Guid.CreateVersion7();

        await using (var session = await PostgresSession.BeginAsync(db.DataSource))
        {
            Assert.True((await _status.ChangeStatusAsync(session, new WalletStatusChangeRequest(
                wallet, WalletStatusCommand.Freeze, "Suspected fraud, case 42", LedgerActor.User(officer)))).IsSuccess);
            await session.CommitAsync();
        }

        await using var connection = await db.DataSource.OpenConnectionAsync();
        var payload = await connection.QuerySingleAsync<string>(
            "SELECT payload::text FROM async.outbox_events WHERE aggregate_id = @id AND event_type = 'WALLET_STATUS_CHANGED'",
            new { id = wallet.Value });
        using var json = System.Text.Json.JsonDocument.Parse(payload);
        Assert.Equal("ACTIVE", json.RootElement.GetProperty("previousStatus").GetString());
        Assert.Equal("FROZEN", json.RootElement.GetProperty("newStatus").GetString());
        Assert.Equal("Suspected fraud, case 42", json.RootElement.GetProperty("reason").GetString());
        Assert.Equal(officer, json.RootElement.GetProperty("actorId").GetGuid());
    }

    [Fact]
    public async Task Reason_is_required()
    {
        var wallet = await _h.NewFundedWallet(0m);

        Assert.Equal(ErrorCodes.Required, (await Change(wallet, WalletStatusCommand.Freeze, reason: " ")).Error.Code);
    }

    private async Task<Result<WalletStatusChangeResult>> Change(WalletId wallet, WalletStatusCommand command, string reason = "operations request")
    {
        await using var session = await PostgresSession.BeginAsync(db.DataSource);
        var result = await _status.ChangeStatusAsync(session, new WalletStatusChangeRequest(wallet, command, reason, LedgerActor.System));
        if (result.IsSuccess)
        {
            await session.CommitAsync();
        }

        return result;
    }

    /// <summary>Debit-adjusts the remaining available balance down to zero (manual correction with approval).</summary>
    private async Task Drain(WalletId wallet, decimal available)
    {
        if (available > 0m)
        {
            Assert.True((await _h.InSession(s => _h.Service.DebitAdjustmentAsync(s, new AdjustmentRequest(
                wallet, $"ADJ-{Guid.NewGuid():N}", Rp(available), "Closing merchant account", "TICKET-CLOSE", Guid.CreateVersion7(), LedgerActor.System)))).IsSuccess);
        }
    }

    private async Task MarkResolved(TransactionId tx)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            "UPDATE core.transactions SET processing_status = 'SUCCESS', financial_status = 'POSTED' WHERE ransys_transaction_id = @id",
            new { id = tx.Value });
    }

    private async Task<TransactionId> OnlyTransactionOf(WalletId wallet)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        return new TransactionId(await connection.QuerySingleAsync<Guid>(
            "SELECT ransys_transaction_id FROM ledger.balance_reservations WHERE wallet_id = @id", new { id = wallet.Value }));
    }

    private async Task<string> StatusOf(WalletId wallet)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<string>("SELECT status FROM ledger.wallets WHERE wallet_id = @id", new { id = wallet.Value });
    }
}
