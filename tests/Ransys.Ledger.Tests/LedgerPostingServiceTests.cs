using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Ledger;
using Ransys.Persistence.PostgreSql;
using Ransys.Testing.PostgreSql;
using static Ransys.Ledger.Tests.LedgerHarness;

namespace Ransys.Ledger.Tests;

/// <summary>
/// Mandatory ledger tests (main.md §15, Ledger Posting Rule Matrix §57) on a real PostgreSQL server.
/// After every scenario the wallet projection must equal the projection reconstructed from the ledger.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LedgerPostingServiceTests(PostgresDatabaseFixture db)
{
    private readonly LedgerHarness _h = new(db);

    [Fact]
    public async Task Same_wallet_race_only_one_reservation_succeeds()
    {
        // main.md §15: available 100,000; A and B each want 80,000.
        var wallet = await _h.NewFundedWallet(100_000m);
        var a = await _h.NewTransaction();
        var b = await _h.NewTransaction();

        var results = await Task.WhenAll(
            Task.Run(() => _h.Reserve(a, wallet, 80_000m)),
            Task.Run(() => _h.Reserve(b, wallet, 80_000m)));

        Assert.Single(results, r => r.IsSuccess);
        Assert.Equal(ErrorCodes.InsufficientBalance, Assert.Single(results, r => r.IsFailure).Error.Code);
        Assert.Equal((100_000m, 20_000m, 80_000m), await _h.Balances(wallet));
        await AssertProjectionMatchesLedger(wallet);
    }

    [Fact]
    public async Task Many_concurrent_reservations_never_overspend()
    {
        var wallet = await _h.NewFundedWallet(100_000m);
        var transactions = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => _h.NewTransaction()));

        var results = await Task.WhenAll(transactions.Select(tx => Task.Run(() => _h.Reserve(tx, wallet, 10_000m))));

        Assert.Equal(10, results.Count(r => r.IsSuccess));
        Assert.All(results.Where(r => r.IsFailure), r => Assert.Equal(ErrorCodes.InsufficientBalance, r.Error.Code));
        Assert.Equal((100_000m, 0m, 100_000m), await _h.Balances(wallet));
        await AssertProjectionMatchesLedger(wallet);
    }

    [Fact]
    public async Task Reserve_moves_available_to_reserved_with_balanced_journal_and_outbox_event()
    {
        var wallet = await _h.NewFundedWallet(1_000_000m);
        var tx = await _h.NewTransaction();

        var result = await _h.Reserve(tx, wallet, 100_000m, fee: 2_500m);

        Assert.Equal(LedgerPostingOutcome.Posted, result.Value.Outcome);
        Assert.Equal((1_000_000m, 897_500m, 102_500m), await _h.Balances(wallet)); // ledger unchanged on reserve
        Assert.Equal(("ACTIVE", "PAYMENT_PROCESSING"), await _h.ReservationState(tx));
        Assert.Equal(
            [("D", 102_500m, $"MERCHANT:{wallet}:AVAILABLE"), ("C", 102_500m, $"MERCHANT:{wallet}:RESERVED")],
            await _h.Entries(PostingKey.Reserve(tx).Value));
        Assert.Equal(["TOPUP_POSTED", "WALLET_RESERVED"], await _h.OutboxEventTypes(wallet));
    }

    [Fact]
    public async Task Duplicate_reserve_has_one_effect_sequentially_and_concurrently()
    {
        var wallet = await _h.NewFundedWallet(100_000m);
        var tx = await _h.NewTransaction();

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => _h.Reserve(tx, wallet, 30_000m))));
        var retry = await _h.Reserve(tx, wallet, 30_000m);

        Assert.All(concurrent, r => Assert.True(r.IsSuccess));
        Assert.Single(concurrent, r => r.Value.Outcome == LedgerPostingOutcome.Posted);
        Assert.Equal(LedgerPostingOutcome.AlreadyPosted, retry.Value.Outcome);
        Assert.Equal(1, await _h.JournalCount(PostingKey.Reserve(tx).Value));
        Assert.Equal((100_000m, 70_000m, 30_000m), await _h.Balances(wallet));
        await AssertProjectionMatchesLedger(wallet);
    }

    [Fact]
    public async Task Same_posting_key_with_different_terms_is_a_conflict()
    {
        var wallet = await _h.NewFundedWallet(0m);
        var reference = $"TP-{Guid.NewGuid():N}";

        Assert.True((await _h.TopUp(wallet, 1_000m, reference)).IsSuccess);
        var conflicting = await _h.TopUp(wallet, 2_000m, reference);

        Assert.Equal(ErrorCodes.PostingKeyConflict, conflicting.Error.Code);
        Assert.Equal((1_000m, 1_000m, 0m), await _h.Balances(wallet));
    }

    [Fact]
    public async Task Payment_success_posts_once_and_consumes_the_reservation()
    {
        // OP-03: DR reserved 102,500 / CR provider payable 100,000 / CR fee revenue 2,500.
        var wallet = await _h.NewFundedWallet(1_000_000m);
        var tx = await _h.NewTransaction();
        await _h.Reserve(tx, wallet, 100_000m, fee: 2_500m);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => _h.Post(tx))));

        Assert.Single(results, r => r.Value.Outcome == LedgerPostingOutcome.Posted);
        Assert.Equal((897_500m, 897_500m, 0m), await _h.Balances(wallet));
        Assert.Equal("COMMITTED", (await _h.ReservationState(tx))!.Value.Status);
        Assert.Equal(
            [
                ("D", 102_500m, $"MERCHANT:{wallet}:RESERVED"),
                ("C", 100_000m, $"SYSTEM:PROVIDER_PAYABLE:{_h.ProviderA}:IDR-V1"),
                ("C", 2_500m, "SYSTEM:RANSYS_FEE_REVENUE:IDR-V1"),
            ],
            await _h.Entries(PostingKey.Post(tx).Value));
        await AssertProjectionMatchesLedger(wallet);
    }

    [Fact]
    public async Task Payment_can_post_an_explicit_provider_tax_and_revenue_split()
    {
        // Ledger Posting Rule Matrix §13 example.
        var wallet = await _h.NewFundedWallet(1_000_000m);
        var tx = await _h.NewTransaction();
        await _h.Reserve(tx, wallet, 100_000m, fee: 2_500m);

        var mismatch = await _h.Post(tx, new PaymentSplit(Rp(99_500m), Rp(2_500m), Rp(250m)));
        var posted = await _h.Post(tx, new PaymentSplit(Rp(99_500m), Rp(2_750m), Rp(250m)));

        Assert.Equal(ErrorCodes.PostingAmountMismatch, mismatch.Error.Code);
        Assert.True(posted.IsSuccess);
        Assert.Contains(("C", 250m, "SYSTEM:TAX_PAYABLE:IDR-V1"), await _h.Entries(PostingKey.Post(tx).Value));
    }

    [Fact]
    public async Task Failure_release_restores_available_exactly()
    {
        var wallet = await _h.NewFundedWallet(1_000_000m);
        var tx = await _h.NewTransaction();
        await _h.Reserve(tx, wallet, 100_000m, fee: 2_500m);

        var released = await _h.Release(tx);
        var duplicate = await _h.Release(tx);

        Assert.Equal(LedgerPostingOutcome.Posted, released.Value.Outcome);
        Assert.Equal(LedgerPostingOutcome.AlreadyPosted, duplicate.Value.Outcome);
        Assert.Equal((1_000_000m, 1_000_000m, 0m), await _h.Balances(wallet));
        Assert.Equal("RELEASED", (await _h.ReservationState(tx))!.Value.Status);
        await AssertProjectionMatchesLedger(wallet);
    }

    [Fact]
    public async Task In_doubt_keeps_the_reservation_active_and_posts_nothing()
    {
        var wallet = await _h.NewFundedWallet(1_000_000m);
        var tx = await _h.NewTransaction();
        await _h.Reserve(tx, wallet, 100_000m, fee: 2_500m);

        await using (var session = await PostgresSession.BeginAsync(db.DataSource))
        {
            Assert.True((await _h.Service.ChangeHoldReasonAsync(session, tx, "IN_DOUBT")).IsSuccess);
            await session.CommitAsync();
        }

        Assert.Equal(("ACTIVE", "IN_DOUBT"), await _h.ReservationState(tx));
        Assert.Equal((1_000_000m, 897_500m, 102_500m), await _h.Balances(wallet));
        Assert.Equal(1, await _h.JournalCount($"TX:{tx}:"));
    }

    [Fact]
    public async Task Post_after_release_and_release_after_post_are_rejected()
    {
        var wallet = await _h.NewFundedWallet(500_000m);
        var released = await _h.NewTransaction();
        var posted = await _h.NewTransaction();
        await _h.Reserve(released, wallet, 100_000m);
        await _h.Reserve(posted, wallet, 100_000m);
        await _h.Release(released);
        await _h.Post(posted);

        Assert.Equal(ErrorCodes.ReservationNotActive, (await _h.Post(released)).Error.Code);
        Assert.Equal(ErrorCodes.ReservationNotActive, (await _h.Release(posted)).Error.Code);
        Assert.Equal(ErrorCodes.ReservationNotActive, (await _h.Release(posted, asReversal: true)).Error.Code);
        Assert.Equal((400_000m, 400_000m, 0m), await _h.Balances(wallet));
        await AssertProjectionMatchesLedger(wallet);
    }

    [Fact]
    public async Task Reversal_while_reserved_releases_with_reversal_release_key()
    {
        var wallet = await _h.NewFundedWallet(200_000m);
        var tx = await _h.NewTransaction();
        await _h.Reserve(tx, wallet, 100_000m);

        var result = await _h.Release(tx, asReversal: true);

        Assert.Equal($"TX:{tx}:REVERSAL_RELEASE", result.Value.PostingKey.Value);
        Assert.Equal((200_000m, 200_000m, 0m), await _h.Balances(wallet));
    }

    [Fact]
    public async Task Reversal_after_post_is_a_compensating_journal_and_leaves_original_untouched()
    {
        var wallet = await _h.NewFundedWallet(1_000_000m);
        var tx = await _h.NewTransaction();
        await _h.Reserve(tx, wallet, 100_000m, fee: 2_500m);
        await _h.Post(tx);
        var originalBefore = await _h.Entries(PostingKey.Post(tx).Value);

        var reversal = await _h.InSession(s => _h.Service.ReversePostedPaymentAsync(s, new ReversePostedPaymentRequest(tx, "RV1")));
        var retry = await _h.InSession(s => _h.Service.ReversePostedPaymentAsync(s, new ReversePostedPaymentRequest(tx, "RV1")));
        var second = await _h.InSession(s => _h.Service.ReversePostedPaymentAsync(s, new ReversePostedPaymentRequest(tx, "RV2")));

        Assert.Equal(LedgerPostingOutcome.Posted, reversal.Value.Outcome);
        Assert.Equal(LedgerPostingOutcome.AlreadyPosted, retry.Value.Outcome);
        Assert.Equal(ErrorCodes.AlreadyReversed, second.Error.Code);
        Assert.Equal(originalBefore, await _h.Entries(PostingKey.Post(tx).Value));
        Assert.Equal(
            [
                ("C", 102_500m, $"MERCHANT:{wallet}:AVAILABLE"),
                ("D", 100_000m, $"SYSTEM:PROVIDER_PAYABLE:{_h.ProviderA}:IDR-V1"),
                ("D", 2_500m, "SYSTEM:RANSYS_FEE_REVENUE:IDR-V1"),
            ],
            await _h.Entries($"TX:{tx}:REVERSAL:RV1"));
        Assert.Equal((1_000_000m, 1_000_000m, 0m), await _h.Balances(wallet));
        await AssertProjectionMatchesLedger(wallet);
    }

    [Fact]
    public async Task Refunds_are_capped_by_the_posted_amount()
    {
        var wallet = await _h.NewFundedWallet(1_000_000m);
        var tx = await _h.NewTransaction();
        await _h.Reserve(tx, wallet, 100_000m, fee: 2_500m);
        await _h.Post(tx);

        Task<Result<LedgerPostingResult>> Refund(string reference, decimal principal, decimal fee, Guid? approval = null) =>
            _h.InSession(s => _h.Service.PostRefundAsync(s, new RefundRequest(
                tx, null, reference, _h.ProviderA, Rp(principal), Rp(fee), approval ?? Guid.CreateVersion7(), LedgerActor.System)));

        Assert.Equal(ErrorCodes.ApprovalRequired, (await Refund("RF0", 1m, 0m, Guid.Empty)).Error.Code);
        Assert.True((await Refund("RF1", 40_000m, 0m)).IsSuccess);                     // OP-12 partial
        Assert.Equal(LedgerPostingOutcome.AlreadyPosted, (await Refund("RF1", 40_000m, 0m)).Value.Outcome);
        Assert.True((await Refund("RF2", 60_000m, 2_500m)).IsSuccess);                 // remaining + fee
        Assert.Equal(ErrorCodes.RefundExceedsPosted, (await Refund("RF3", 0.01m, 0m)).Error.Code);

        Assert.Equal((1_000_000m, 1_000_000m, 0m), await _h.Balances(wallet));
        await AssertProjectionMatchesLedger(wallet);
    }

    [Fact]
    public async Task Merchant_api_refund_is_authorized_only_by_its_own_refund_child()
    {
        // ADR-024: no maker-checker approval, but the authorization must be bound to the refund child transaction.
        var wallet = await _h.NewFundedWallet(1_000_000m);
        var tx = await _h.NewTransaction();
        var child = await _h.NewTransaction();
        await _h.Reserve(tx, wallet, 100_000m, fee: 2_500m);
        await _h.Post(tx);

        Task<Result<LedgerPostingResult>> Refund(TransactionId? refundTransaction, RefundAuthorization authorization) =>
            _h.InSession(s => _h.Service.PostRefundAsync(s, new RefundRequest(
                tx, refundTransaction, $"RF-{Guid.NewGuid():N}", _h.ProviderA, Rp(10_000m), Rp(0m), authorization, LedgerActor.System)));

        var bound = new RefundAuthorization.MerchantApiRequest(child, new ChannelId(Guid.CreateVersion7()), "REF-1");
        Assert.Equal(ErrorCodes.ApprovalRequired, (await Refund(null, bound)).Error.Code);
        Assert.Equal(ErrorCodes.ApprovalRequired, (await Refund(await _h.NewTransaction(), bound)).Error.Code);
        Assert.Equal(ErrorCodes.ApprovalRequired, (await Refund(tx, new RefundAuthorization.MerchantApiRequest(tx, bound.ChannelId, "REF-1"))).Error.Code);
        Assert.Equal(ErrorCodes.ApprovalRequired, (await Refund(child, new RefundAuthorization.ApprovedRequest(Guid.Empty))).Error.Code);

        var posted = await Refund(child, bound);

        Assert.Equal(LedgerPostingOutcome.Posted, posted.Value.Outcome);
        Assert.Equal((907_500m, 907_500m, 0m), await _h.Balances(wallet));
        await AssertProjectionMatchesLedger(wallet);
    }

    [Fact]
    public async Task Manual_adjustments_require_approval_and_never_create_negative_balance()
    {
        var wallet = await _h.NewFundedWallet(40_000m);

        Task<Result<LedgerPostingResult>> Adjust(bool credit, decimal amount, Guid approval) =>
            _h.InSession(s =>
            {
                var request = new AdjustmentRequest(
                    wallet, $"ADJ-{Guid.NewGuid():N}", Rp(amount), "Validated exception", "TICKET-1", approval, LedgerActor.User(Guid.CreateVersion7()));
                return credit ? _h.Service.CreditAdjustmentAsync(s, request) : _h.Service.DebitAdjustmentAsync(s, request);
            });

        Assert.Equal(ErrorCodes.ApprovalRequired, (await Adjust(true, 50_000m, Guid.Empty)).Error.Code);
        Assert.True((await Adjust(true, 50_000m, Guid.CreateVersion7())).IsSuccess);           // OP-13
        Assert.Equal(ErrorCodes.InsufficientBalance, (await Adjust(false, 90_000.01m, Guid.CreateVersion7())).Error.Code);
        Assert.True((await Adjust(false, 90_000m, Guid.CreateVersion7())).IsSuccess);          // OP-14

        Assert.Equal((0m, 0m, 0m), await _h.Balances(wallet));
        await AssertProjectionMatchesLedger(wallet);
    }

    [Fact]
    public async Task Rolled_back_session_leaves_no_trace()
    {
        var wallet = await _h.NewFundedWallet(100_000m);
        var tx = await _h.NewTransaction();

        await using (var session = await PostgresSession.BeginAsync(db.DataSource))
        {
            var result = await _h.Service.ReserveAsync(session, new ReserveRequest(tx, wallet, Rp(50_000m), Rp(0m), "PAYMENT_PROCESSING"));
            Assert.True(result.IsSuccess);
            // disposed without commit: journal, reservation, wallet and outbox are rolled back together
        }

        Assert.Equal((100_000m, 100_000m, 0m), await _h.Balances(wallet));
        Assert.Null(await _h.ReservationState(tx));
        Assert.Equal(0, await _h.JournalCount($"TX:{tx}:"));
        Assert.Equal(["TOPUP_POSTED"], await _h.OutboxEventTypes(wallet));
    }

    [Fact]
    public async Task Unknown_wallet_and_missing_reservation_are_controlled_failures()
    {
        var tx = await _h.NewTransaction();

        Assert.Equal(ErrorCodes.WalletNotFound, (await _h.Reserve(tx, new WalletId(Guid.CreateVersion7()), 1m)).Error.Code);
        Assert.Equal(ErrorCodes.ReservationNotFound, (await _h.Post(tx)).Error.Code);
        Assert.Equal(ErrorCodes.ReservationNotFound, (await _h.Release(tx)).Error.Code);
    }

    private async Task AssertProjectionMatchesLedger(WalletId wallet)
    {
        var (ledger, available, reserved) = await _h.Balances(wallet);
        var (ledgerAvailable, ledgerReserved) = await _h.BalancesFromLedger(wallet);

        Assert.True(available >= 0 && reserved >= 0, "wallet balances must never be negative");
        Assert.Equal(available + reserved, ledger);
        Assert.Equal((available, reserved), (ledgerAvailable, ledgerReserved));
    }
}
