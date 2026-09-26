using Ransys.Domain;
using Ransys.Domain.Attempts;
using Ransys.Domain.Common;
using Ransys.Domain.Routing;
using Ransys.Domain.Transactions;
using Ransys.Testing.PostgreSql;
using Ransys.TransactionCore.Finalization;
using Ransys.TransactionCore.Idempotency;
using Ransys.TransactionCore.Reversal;
using static Ransys.IntegrationTests.CoreHarness;

namespace Ransys.IntegrationTests.Reversal;

/// <summary>
/// ADR-012 end to end on a real PostgreSQL server: reversal as a child transaction; the original's state is never
/// overwritten while the reversal runs; the financial effect is booked on the original when the child is confirmed.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReversalFlowTests(PostgresDatabaseFixture db)
{
    private readonly CoreHarness _h = new(db);

    [Fact]
    public async Task Reversal_of_an_in_doubt_payment_releases_the_hold_without_touching_the_original_meanwhile()
    {
        var (original, wallet) = await InDoubtPayment();

        var child = await Start(original);
        Assert.Equal((ProcessingStatus.InDoubt, FinancialStatus.Reserved), await Status(original)); // not overwritten
        Assert.Equal(ProcessingStatus.Processing, (await _h.Load(child)).ProcessingStatus);
        await SendReversalRequest(child);

        Ok(await _h.Finalization.ApplyAsync(Confirm(child)));

        Assert.Equal((ProcessingStatus.Reversed, FinancialStatus.Released), await Status(original));
        Assert.Equal((ProcessingStatus.Success, FinancialStatus.None), await Status(child));
        Assert.Equal(1, await Journals($"TX:{original}:REVERSAL_RELEASE"));
        Assert.Equal((1_000_000m, 1_000_000m, 0m), await Balances(wallet));
    }

    [Fact]
    public async Task Reversal_of_a_posted_payment_is_a_compensating_journal_keyed_by_the_child()
    {
        var (original, wallet) = await _h.ProcessingPayment();
        Ok(await _h.Finalization.ApplyAsync(new ProviderResultCommand(original, AttemptResolutionKind.Success, ChangeSource.Callback, "PROVIDER_SUCCESS")));

        var child = await Start(original);
        Assert.Equal((ProcessingStatus.Success, FinancialStatus.Posted), await Status(original));
        Ok(await _h.Finalization.ApplyAsync(Confirm(child)));

        Assert.Equal((ProcessingStatus.Reversed, FinancialStatus.Reversed), await Status(original));
        Assert.Equal(1, await Journals($"TX:{original}:REVERSAL:{child}"));
        Assert.Equal((1_000_000m, 1_000_000m, 0m), await Balances(wallet));
    }

    [Fact]
    public async Task Declined_reversal_fails_only_the_child_and_a_new_reversal_can_follow()
    {
        var (original, _) = await _h.ProcessingPayment();
        Ok(await _h.Finalization.ApplyAsync(new ProviderResultCommand(original, AttemptResolutionKind.Success, ChangeSource.Callback, "PROVIDER_SUCCESS")));
        var first = await Start(original);

        Ok(await _h.Finalization.ApplyAsync(new ProviderResultCommand(first, AttemptResolutionKind.Failed, ChangeSource.SyncProviderResponse, ReasonCodes.ReversalDeclined)));

        Assert.Equal((ProcessingStatus.Failed, FinancialStatus.None), await Status(first));
        Assert.Equal((ProcessingStatus.Success, FinancialStatus.Posted), await Status(original));
        var second = await Start(original);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Original_result_arriving_while_the_reversal_runs_is_applied_and_the_reversal_then_compensates()
    {
        // The interleaving ADR-012 used to reject.
        var (original, wallet) = await InDoubtPayment();
        var child = await Start(original);

        var late = await _h.Finalization.ApplyAsync(new ProviderResultCommand(original, AttemptResolutionKind.Success, ChangeSource.Callback, "CALLBACK_SUCCESS"));
        Assert.Equal(TransitionKind.Applied, late.Value.Kind);
        Assert.Equal((ProcessingStatus.Success, FinancialStatus.Posted), await Status(original));

        Ok(await _h.Finalization.ApplyAsync(Confirm(child)));

        Assert.Equal((ProcessingStatus.Reversed, FinancialStatus.Reversed), await Status(original));
        Assert.Equal(1, await Journals($"TX:{original}:POST"));
        Assert.Equal(1, await Journals($"TX:{original}:REVERSAL:{child}"));
        Assert.Equal((1_000_000m, 1_000_000m, 0m), await Balances(wallet));
    }

    [Fact]
    public async Task Only_one_open_reversal_per_original_and_retries_are_idempotent()
    {
        var (original, _) = await InDoubtPayment();
        var reference = $"REV-{Guid.NewGuid():N}";

        var first = await StartRaw(original, reference);
        var retry = await StartRaw(original, reference);
        var another = await StartRaw(original, $"REV-{Guid.NewGuid():N}");

        Assert.Equal(IdempotencyOutcome.New, first.Value.Outcome);
        Assert.Equal((IdempotencyOutcome.ExistingTransaction, first.Value.ReversalTransactionId), (retry.Value.Outcome, retry.Value.ReversalTransactionId));
        Assert.Equal(ErrorCodes.ReversalAlreadyActive, another.Error.Code);
    }

    [Fact]
    public async Task Provider_without_reversal_capability_cannot_be_asked_to_reverse()
    {
        var (original, _) = await _h.ProcessingPayment(provider: _h.ProviderB); // BANK_B has no REVERSAL capability
        Ok(await _h.Finalization.ApplyAsync(new ProviderResultCommand(original, AttemptResolutionKind.InDoubt, ChangeSource.SyncProviderResponse, "PROVIDER_READ_TIMEOUT")));

        var result = await StartRaw(original, $"REV-{Guid.NewGuid():N}");

        Assert.Equal(ErrorCodes.ReversalNotSupported, result.Error.Code);
    }

    [Fact]
    public async Task Non_reversible_original_is_rejected()
    {
        var (original, _) = await _h.ProcessingPayment(); // PROCESSING: request still in flight

        Assert.Equal(ErrorCodes.ReversalNotAllowed, (await StartRaw(original, $"REV-{Guid.NewGuid():N}")).Error.Code);
    }

    [Fact]
    public async Task Reversal_confirmation_racing_the_original_result_always_nets_to_zero()
    {
        // Parent → child lock order: either order of arrival is consistent and deadlock-free.
        var cases = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            var (original, wallet) = await InDoubtPayment();
            return (original, wallet, child: await Start(original));
        }));

        await Task.WhenAll(cases.SelectMany(c => new[]
        {
            Task.Run(() => _h.Finalization.ApplyAsync(Confirm(c.child))),
            Task.Run(() => _h.Finalization.ApplyAsync(new ProviderResultCommand(c.original, AttemptResolutionKind.Success, ChangeSource.Callback, "CALLBACK_SUCCESS"))),
        }));

        foreach (var (original, wallet, _) in cases)
        {
            Assert.Equal(ProcessingStatus.Reversed, (await _h.Load(original)).ProcessingStatus);
            Assert.Equal((1_000_000m, 1_000_000m, 0m), await Balances(wallet));
        }
    }

    private async Task<(TransactionId Original, WalletId Wallet)> InDoubtPayment()
    {
        await _h.GrantCapability(_h.ProviderA.ProviderId, ProviderCapabilities.Reversal);
        var (original, wallet) = await _h.ProcessingPayment();
        Ok(await _h.Finalization.ApplyAsync(new ProviderResultCommand(original, AttemptResolutionKind.InDoubt, ChangeSource.SyncProviderResponse, "PROVIDER_READ_TIMEOUT")));
        return (original, wallet);
    }

    private async Task<TransactionId> Start(TransactionId original)
    {
        await _h.GrantCapability(_h.ProviderA.ProviderId, ProviderCapabilities.Reversal);
        return Ok(await StartRaw(original, $"REV-{Guid.NewGuid():N}")).ReversalTransactionId;
    }

    private async Task<Result<ReversalStarted>> StartRaw(TransactionId original, string reference)
    {
        await using var session = await _h.Session();
        var result = await _h.Reversals.StartAsync(session, new StartReversalCommand(original, reference, null, "REVERSAL_REQUESTED", ChangeSource.ManualAction));
        if (result.IsSuccess)
        {
            await session.CommitAsync();
        }

        return result;
    }

    private async Task SendReversalRequest(TransactionId child)
    {
        await using var session = await _h.Session();
        var transaction = Ok(await _h.Transactions.GetAsync(session, child, Persistence.PostgreSql.Transactions.RowLock.ForUpdate))!;
        Ok(await _h.Attempts.StartAsync(session, transaction, AttemptType.Reversal, transaction.Routing!.CurrentProvider, "corr", "trace"));
        await session.CommitAsync();
    }

    private static ProviderResultCommand Confirm(TransactionId child) =>
        new(child, AttemptResolutionKind.Success, ChangeSource.SyncProviderResponse, "REVERSAL_CONFIRMED");

    private async Task<(ProcessingStatus, FinancialStatus)> Status(TransactionId id)
    {
        var t = await _h.Load(id);
        return (t.ProcessingStatus, t.FinancialStatus);
    }

    private Task<int> Journals(string postingKey) =>
        _h.Query<int>("SELECT count(*) FROM ledger.ledger_transactions WHERE posting_key = @postingKey", new { postingKey });

    private Task<(decimal Ledger, decimal Available, decimal Reserved)> Balances(WalletId wallet) =>
        _h.Query<(decimal, decimal, decimal)>(
            "SELECT ledger_balance, available_balance, reserved_balance FROM ledger.wallets WHERE wallet_id = @id", new { id = wallet.Value });
}
