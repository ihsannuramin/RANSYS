using Ransys.Domain;
using Ransys.Domain.Attempts;
using Ransys.Domain.Common;
using Ransys.Domain.Transactions;
using Ransys.Testing.PostgreSql;
using Ransys.TransactionCore.Attempts;
using Ransys.TransactionCore.Finalization;
using static Ransys.IntegrationTests.CoreHarness;

namespace Ransys.IntegrationTests.Concurrency;

/// <summary>
/// Cross-component concurrency on a real PostgreSQL server (main.md §22, State Transition Matrix §55–58,
/// Ledger Posting Rule Matrix §39–40). Every scenario ends with the financial invariants: at most one financial
/// outcome per transaction, never both POST and RELEASE, and the wallet projection equal to the ledger.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CrossComponentConcurrencyTests(PostgresDatabaseFixture db)
{
    private readonly CoreHarness _h = new(db);

    [Fact]
    public async Task Duplicate_provider_success_from_several_sources_posts_exactly_once()
    {
        var (tx, wallet) = await _h.ProcessingPayment();
        ChangeSource[] sources = [ChangeSource.SyncProviderResponse, ChangeSource.Callback, ChangeSource.Callback, ChangeSource.StatusCheck,
            ChangeSource.StatusCheck, ChangeSource.Advice, ChangeSource.Reconciliation, ChangeSource.Callback];

        var results = await Task.WhenAll(sources.Select(source =>
            Task.Run(() => _h.Finalization.ApplyAsync(Result(tx, AttemptResolutionKind.Success, source)))));

        Assert.All(results, r => Assert.True(r.IsSuccess, r.IsFailure ? r.Error.ToString() : null));
        Assert.Single(results, r => r.Value.Kind == TransitionKind.Applied);
        Assert.Equal(sources.Length - 1, results.Count(r => r.Value.Kind == TransitionKind.NoChange));
        await AssertFinal(tx, wallet, ProcessingStatus.Success, FinancialStatus.Posted, posts: 1, releases: 0);
        Assert.Equal(1, await EventCount(tx, "TRANSACTION_SUCCEEDED"));
    }

    [Fact]
    public async Task Duplicate_provider_failure_releases_exactly_once()
    {
        var (tx, wallet) = await _h.ProcessingPayment();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => _h.Finalization.ApplyAsync(Result(tx, AttemptResolutionKind.Failed, ChangeSource.Callback)))));

        Assert.Single(results, r => r.Value.Kind == TransitionKind.Applied);
        await AssertFinal(tx, wallet, ProcessingStatus.Failed, FinancialStatus.Released, posts: 0, releases: 1);
    }

    [Fact]
    public async Task Status_check_success_racing_reconciliation_failure_never_moves_money_twice()
    {
        // Ten IN_DOUBT transactions; for each, a status check says SUCCESS while reconciliation says NOT PROCESSED.
        var transactions = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            var (tx, wallet) = await _h.ProcessingPayment();
            Ok(await _h.Finalization.ApplyAsync(Result(tx, AttemptResolutionKind.InDoubt, ChangeSource.SyncProviderResponse, "PROVIDER_READ_TIMEOUT")));
            return (tx, wallet);
        }));

        await Task.WhenAll(transactions.SelectMany(t => new[]
        {
            Task.Run(() => _h.Finalization.ApplyAsync(Result(t.tx, AttemptResolutionKind.Success, ChangeSource.StatusCheck, "STATUS_CHECK_SUCCESS"))),
            Task.Run(() => _h.Finalization.ApplyAsync(Result(t.tx, AttemptResolutionKind.Failed, ChangeSource.Reconciliation, "RECON_CONFIRMED_FAILURE"))),
        }));

        foreach (var (tx, wallet) in transactions)
        {
            var after = await _h.Load(tx);
            var (posts, releases) = await FinancialEffects(tx);

            // Whichever arrived first decided the truth; the contradiction became a reconciliation exception (§53–56).
            Assert.Equal(1, posts + releases);
            Assert.Equal(ReconciliationStatus.Exception, after.ReconciliationStatus);
            Assert.True(
                (after.ProcessingStatus, after.FinancialStatus, posts) is (ProcessingStatus.Success, FinancialStatus.Posted, 1)
                || (after.ProcessingStatus, after.FinancialStatus, releases) is (ProcessingStatus.Failed, FinancialStatus.Released, 1),
                $"{tx}: {after.ProcessingStatus}/{after.FinancialStatus} posts={posts} releases={releases}");
            await AssertProjectionMatchesLedger(wallet);
        }
    }

    [Fact]
    public async Task Callback_racing_the_recovery_worker_always_ends_success_posted_once()
    {
        // Core crashed during the provider call (attempt without outcome); the provider's success callback arrives
        // while the recovery worker is closing the attempt as possibly sent.
        var cases = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            var (tx, wallet) = await _h.ProcessingPayment();
            await using var session = await _h.Session();
            var transaction = Ok(await _h.Transactions.GetAsync(session, tx, Persistence.PostgreSql.Transactions.RowLock.ForUpdate))!;
            var attempt = Ok(await _h.Attempts.StartAsync(session, transaction, AttemptType.Payment, _h.ProviderA, "corr", "trace"));
            await session.CommitAsync();
            return (tx, wallet, attempt.Id);
        }));

        await Task.WhenAll(cases.SelectMany(c => new Task[]
        {
            Task.Run(() => _h.Finalization.ApplyAsync(Result(c.tx, AttemptResolutionKind.Success, ChangeSource.Callback, "CALLBACK_SUCCESS"))),
            Task.Run(async () =>
            {
                await using var session = await _h.Session();
                var recovered = await _h.Recovery.RecoverAsync(session, c.tx, c.Id);
                Assert.True(recovered.IsSuccess, recovered.IsFailure ? recovered.Error.ToString() : null);
                await session.CommitAsync();
            }),
        }));

        foreach (var (tx, wallet, attemptId) in cases)
        {
            await AssertFinal(tx, wallet, ProcessingStatus.Success, FinancialStatus.Posted, posts: 1, releases: 0);
            Assert.True(Assert.Single(await _h.LoadAttempts(tx)).IsOutcomeRecorded);
        }
    }

    [Fact]
    public async Task Concurrent_reservations_and_finalizations_on_one_wallet_do_not_deadlock()
    {
        // Lock order (transaction → wallet → reservation → posting key) is shared by reserve and finalization paths.
        var wallet = await _h.NewFundedWallet(5_000_000m);
        var flows = await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(async () =>
        {
            var (tx, _) = await _h.ProcessingPayment(wallet);
            var resolution = i % 3 == 0 ? AttemptResolutionKind.Failed : AttemptResolutionKind.Success;
            return (Tx: tx, Result: await _h.Finalization.ApplyAsync(Result(tx, resolution, ChangeSource.SyncProviderResponse)));
        })));

        Assert.All(flows, f => Assert.True(f.Result.IsSuccess, f.Result.IsFailure ? f.Result.Error.ToString() : null));
        var successes = flows.Count(f => f.Result.Value.ProcessingStatus == ProcessingStatus.Success);
        Assert.Equal(16, successes);

        var (ledger, available, reserved) = await Balances(wallet);
        Assert.Equal(0m, reserved);
        Assert.Equal(5_000_000m - (successes * 102_500m), ledger);
        Assert.Equal(ledger, available);
        await AssertProjectionMatchesLedger(wallet);
    }

    [Fact]
    public async Task Late_timeout_after_success_is_rejected_without_side_effects()
    {
        var (tx, wallet) = await _h.ProcessingPayment();
        Ok(await _h.Finalization.ApplyAsync(Result(tx, AttemptResolutionKind.Success, ChangeSource.Callback)));

        var late = await _h.Finalization.ApplyAsync(Result(tx, AttemptResolutionKind.InDoubt, ChangeSource.SyncProviderResponse, "PROVIDER_READ_TIMEOUT"));

        Assert.Equal(ErrorCodes.InvalidStateTransition, late.Error.Code);
        await AssertFinal(tx, wallet, ProcessingStatus.Success, FinancialStatus.Posted, posts: 1, releases: 0);
    }

    private static ProviderResultCommand Result(
        TransactionId tx, AttemptResolutionKind resolution, ChangeSource source, string? reason = null) =>
        new(tx, resolution, source, reason ?? $"PROVIDER_{resolution.ToString().ToUpperInvariant()}");

    private async Task AssertFinal(
        TransactionId tx, WalletId wallet, ProcessingStatus processing, FinancialStatus financial, int posts, int releases)
    {
        var after = await _h.Load(tx);
        Assert.Equal((processing, financial), (after.ProcessingStatus, after.FinancialStatus));
        Assert.Equal((posts, releases), await FinancialEffects(tx));
        await AssertProjectionMatchesLedger(wallet);
    }

    private Task<(int Posts, int Releases)> FinancialEffects(TransactionId tx) =>
        _h.Query<(int, int)>(
            """
            SELECT count(*) FILTER (WHERE operation_type = 'POST'), count(*) FILTER (WHERE operation_type = 'RELEASE')
            FROM ledger.ledger_transactions WHERE ransys_transaction_id = @id
            """,
            new { id = tx.Value });

    private Task<int> EventCount(TransactionId tx, string eventType) =>
        _h.Query<int>(
            "SELECT count(*) FROM async.outbox_events WHERE aggregate_id = @id AND event_type = @eventType",
            new { id = tx.Value, eventType });

    private Task<(decimal Ledger, decimal Available, decimal Reserved)> Balances(WalletId wallet) =>
        _h.Query<(decimal, decimal, decimal)>(
            "SELECT ledger_balance, available_balance, reserved_balance FROM ledger.wallets WHERE wallet_id = @id",
            new { id = wallet.Value });

    private async Task AssertProjectionMatchesLedger(WalletId wallet)
    {
        var (ledger, available, reserved) = await Balances(wallet);
        var fromLedger = await _h.Query<(decimal Available, decimal Reserved)>(
            """
            SELECT
              COALESCE(SUM(CASE WHEN a.account_code = @available THEN (CASE e.entry_side WHEN 'C' THEN e.amount ELSE -e.amount END) END), 0),
              COALESCE(SUM(CASE WHEN a.account_code = @reserved THEN (CASE e.entry_side WHEN 'C' THEN e.amount ELSE -e.amount END) END), 0)
            FROM ledger.ledger_entries e
            JOIN ledger.ledger_accounts a ON a.ledger_account_id = e.ledger_account_id
            WHERE a.wallet_id = @wallet
            """,
            new { wallet = wallet.Value, available = $"MERCHANT:{wallet}:AVAILABLE", reserved = $"MERCHANT:{wallet}:RESERVED" });

        Assert.True(available >= 0 && reserved >= 0, "wallet balances must never be negative");
        Assert.Equal(available + reserved, ledger);
        Assert.Equal((available, reserved), fromLedger);
    }
}
