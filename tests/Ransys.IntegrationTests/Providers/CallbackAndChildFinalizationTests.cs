using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Ransys.Adapter.Contracts.V1;
using Ransys.Domain;
using Ransys.Domain.Attempts;
using Ransys.Domain.Fees;
using Ransys.Domain.Transactions;
using Ransys.Persistence.PostgreSql.Queries;
using Ransys.Testing.PostgreSql;
using Ransys.TransactionCore.Finalization;
using Ransys.TransactionCore.Providers;
using static Ransys.IntegrationTests.CoreHarness;
using ProviderOutcome = Ransys.Adapter.Contracts.V1.ProviderOutcome;
using TransportStatus = Ransys.Adapter.Contracts.V1.TransportStatus;

namespace Ransys.IntegrationTests.Providers;

/// <summary>
/// M12c on a real PostgreSQL server: the V1 callback sink (duplicate, conflict, unknown, provider mismatch, NOT_SENT) and
/// finalization of REFUND (ADR-023/024) and VOID (ADR-019) children.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CallbackAndChildFinalizationTests(PostgresDatabaseFixture db)
{
    private readonly CoreHarness _h = new(db);

    [Fact]
    public async Task Callback_success_posts_once_duplicate_is_no_change_and_contradiction_is_a_recon_exception()
    {
        var (payment, wallet) = await _h.ProcessingPayment();
        await _h.StartAttempt(payment, _h.ProviderA);

        var first = await _h.CallbackSink.SubmitAsync(Callback(payment, ProviderOutcome.Success), CancellationToken.None);
        var duplicate = await _h.CallbackSink.SubmitAsync(Callback(payment, ProviderOutcome.Success), CancellationToken.None);
        var conflict = await _h.CallbackSink.SubmitAsync(Callback(payment, ProviderOutcome.Failed), CancellationToken.None);

        Assert.Equal((true, ProviderCallbackSink.Applied), (first.Accepted, first.AcknowledgementCode));
        Assert.Equal((true, ProviderCallbackSink.Duplicate), (duplicate.Accepted, duplicate.AcknowledgementCode));
        Assert.Equal((true, ProviderCallbackSink.ConflictRecorded), (conflict.Accepted, conflict.AcknowledgementCode));
        var loaded = await _h.Load(payment);
        Assert.Equal((ProcessingStatus.Success, FinancialStatus.Posted, ReconciliationStatus.Exception),
            (loaded.ProcessingStatus, loaded.FinancialStatus, loaded.ReconciliationStatus));
        Assert.Equal(1, await Journals($"TX:{payment}:POST"));
        Assert.Equal((897_500m, 897_500m, 0m), await Balances(wallet));
    }

    [Fact]
    public async Task Unknown_transaction_other_provider_and_not_sent_callbacks_are_not_accepted()
    {
        var (payment, _) = await _h.ProcessingPayment();

        var unknown = await _h.CallbackSink.SubmitAsync(Callback(new TransactionId(Guid.CreateVersion7()), ProviderOutcome.Success), CancellationToken.None);
        var otherProvider = await _h.CallbackSink.SubmitAsync(
            Callback(payment, ProviderOutcome.Success) with { ProviderId = db.Seed.ProviderBId }, CancellationToken.None);
        var notSent = await _h.CallbackSink.SubmitAsync(
            Callback(payment, ProviderOutcome.NotSent, ResultFinality.NotApplicable, TransportStatus.NotSent, requestSent: false), CancellationToken.None);

        Assert.Equal((false, ProviderCallbackSink.UnknownTransaction), (unknown.Accepted, unknown.AcknowledgementCode));
        Assert.Equal((false, ProviderCallbackSink.ProviderMismatch), (otherProvider.Accepted, otherProvider.AcknowledgementCode));
        Assert.Equal((false, ProviderCallbackSink.NotSentInvalid), (notSent.Accepted, notSent.AcknowledgementCode));
        Assert.Equal(ProcessingStatus.Processing, (await _h.Load(payment)).ProcessingStatus);
    }

    [Fact]
    public async Task Pending_callback_marks_pending_and_a_late_pending_after_success_is_ignored()
    {
        var (payment, _) = await _h.ProcessingPayment();
        await _h.StartAttempt(payment, _h.ProviderA);

        var pending = await _h.CallbackSink.SubmitAsync(Callback(payment, ProviderOutcome.Pending, ResultFinality.NonFinal), CancellationToken.None);
        Assert.Equal(ProcessingStatus.Pending, (await _h.Load(payment)).ProcessingStatus);
        await _h.CallbackSink.SubmitAsync(Callback(payment, ProviderOutcome.Success), CancellationToken.None);
        var late = await _h.CallbackSink.SubmitAsync(Callback(payment, ProviderOutcome.Pending, ResultFinality.NonFinal), CancellationToken.None);

        Assert.True(pending.Accepted);
        Assert.Equal((true, ProviderCallbackSink.StaleIgnored), (late.Accepted, late.AcknowledgementCode));
        Assert.Equal(ProcessingStatus.Success, (await _h.Load(payment)).ProcessingStatus);
    }

    [Fact]
    public async Task Impossible_callback_result_is_treated_as_in_doubt_and_keeps_the_hold()
    {
        var (payment, wallet) = await _h.ProcessingPayment();
        await _h.StartAttempt(payment, _h.ProviderA);
        var impossible = Callback(payment, ProviderOutcome.Success, transport: TransportStatus.Timeout);

        var ack = await _h.CallbackSink.SubmitAsync(impossible, CancellationToken.None);

        Assert.True(ack.Accepted);
        var loaded = await _h.Load(payment);
        Assert.Equal((ProcessingStatus.InDoubt, FinancialStatus.Reserved), (loaded.ProcessingStatus, loaded.FinancialStatus));
        Assert.Equal("1002", loaded.ResponseCode);
        Assert.Equal((1_000_000m, 897_500m, 102_500m), await Balances(wallet));
    }

    /// <summary>
    /// R2: the callback sink must persist provider evidence (reference/STAN/RRN) on the attempt exactly once, in the same
    /// database transaction as the status update, and never drop it. This covers the attempt-has-no-recorded-outcome-yet
    /// case (e.g. a crash before the sync path could record anything): the attempt is SENT with no outcome at all, so
    /// the callback is the first and only report and simply records it. It does <em>not</em> cover a callback arriving
    /// after some outcome (timeout/PENDING/synthetic recovery) was already recorded on the attempt — that is a
    /// different, later-evidence scenario covered by ADR-027 / <see cref="TransactionResultProjection"/> and the
    /// dedicated tests below.
    /// </summary>
    [Fact]
    public async Task Callback_success_persists_provider_evidence_on_the_attempt_for_reuse_by_child_requests()
    {
        var (payment, _) = await _h.ProcessingPayment();
        var attempt = await _h.StartAttempt(payment, _h.ProviderA); // SENT, no outcome recorded yet (e.g. crash before sync path recorded one)
        Assert.False(attempt.IsOutcomeRecorded);

        var ack = await _h.CallbackSink.SubmitAsync(
            Callback(payment, ProviderOutcome.Success, providerReference: "PRV-REF-999", providerStan: "123456", providerRrn: "RRN000111"),
            CancellationToken.None);

        Assert.Equal((true, ProviderCallbackSink.Applied), (ack.Accepted, ack.AcknowledgementCode));
        Assert.Equal(ProcessingStatus.Success, (await _h.Load(payment)).ProcessingStatus);

        var reloaded = Assert.Single(await _h.LoadAttempts(payment));
        Assert.True(reloaded.IsOutcomeRecorded);
        Assert.Equal(
            ("PRV-REF-999", "123456", "RRN000111"),
            (reloaded.Outcome!.ProviderReference, reloaded.Outcome.ProviderStan, reloaded.Outcome.ProviderRrn));

        var recordedAt = await _h.Query<DateTime?>(
            "SELECT outcome_recorded_at FROM core.transaction_attempts WHERE transaction_attempt_id = @id", new { id = attempt.Id.Value });
        Assert.NotNull(recordedAt);

        // A refund/reversal/void child of this original would carry the same evidence (ProviderRequestFactory.cs).
        var forChild = OriginalProviderReferences.From(await _h.LoadAttempts(payment));
        Assert.Equal(("PRV-REF-999", "123456", "RRN000111"), (forChild.ProviderReference, forChild.ProviderStan, forChild.ProviderRrn));
    }

    /// <summary>
    /// R1: a callback must be validated and applied under one continuous lock. Reproduces the race by ordering the
    /// pieces exactly as a concurrent failover would: attempt A is proven NOT_SENT and the transaction fails over to
    /// provider B with a new attempt, committed; only then does a callback claiming SUCCESS from provider A arrive. It
    /// must not be blindly applied — A is no longer the routed provider, so nothing about B's context is affected.
    /// </summary>
    [Fact]
    public async Task Stale_callback_from_a_provider_superseded_by_failover_is_rejected()
    {
        var (payment, wallet) = await _h.ProcessingPayment(); // routed to Provider A
        var attemptA = await _h.StartAttempt(payment, _h.ProviderA);

        await using (var session = await _h.Session())
        {
            var transaction = Ok(await _h.Transactions.GetAsync(session, payment, Persistence.PostgreSql.Transactions.RowLock.ForUpdate))!;
            var notSent = AttemptOutcome.Create(false, Domain.Attempts.TransportStatus.ConnectionError).Value;
            Ok(await _h.Attempts.RecordOutcomeAsync(session, attemptA, notSent));
            var attempts = Ok(await _h.AttemptStore.GetByTransactionAsync(session, payment));
            Assert.True(transaction.RecordFailover(attempts[0], _h.ProviderB, "PROVIDER_LINK_DOWN", DateTimeOffset.UtcNow).IsSuccess);
            Ok(await _h.Attempts.StartAsync(session, transaction, AttemptType.Payment, _h.ProviderB, "corr-2", "trace-2"));
            Ok(await _h.Transactions.UpdateAsync(session, transaction));
            await session.CommitAsync();
        }

        // A late report from provider A, now superseded: correlation must not misapply it against B's context.
        var stale = await _h.CallbackSink.SubmitAsync(Callback(payment, ProviderOutcome.Success), CancellationToken.None);

        Assert.Equal((false, ProviderCallbackSink.ProviderMismatch), (stale.Accepted, stale.AcknowledgementCode));
        var after = await _h.Load(payment);
        Assert.Equal(ProcessingStatus.Processing, after.ProcessingStatus); // never wrongly completed from A's stale report
        Assert.Equal(_h.ProviderB, after.Routing!.CurrentProvider);
        Assert.Equal(0, await Journals($"TX:{payment}:POST")); // no posting was ever made from the stale callback
        Assert.Equal((1_000_000m, 897_500m, 102_500m), await Balances(wallet)); // the hold is untouched
    }

    [Fact]
    public async Task Refund_children_post_pro_rata_fees_cumulatively_and_complete_the_original()
    {
        var (payment, wallet) = await PostedPayment(FeeRefundPolicy.ProRata);
        var first = await _h.ProcessingChild(payment, TransactionType.Refund, 40_000m);

        // The callback correlates to the child: the provider answered the child's own refund request.
        var ack = await _h.CallbackSink.SubmitAsync(Callback(first, ProviderOutcome.Success), CancellationToken.None);

        Assert.True(ack.Accepted);
        Assert.Equal((ProcessingStatus.PartiallyRefunded, FinancialStatus.PartiallyRefunded), await Status(payment));
        Assert.Equal((ProcessingStatus.Success, FinancialStatus.None), await Status(first));
        Assert.Equal(41_000m, await JournalTotal($"TX:{payment}:REFUND:{first}"));            // 40,000 + 2,500 × 40%
        Assert.Equal((938_500m, 938_500m, 0m), await Balances(wallet));
        Assert.Null(await _h.Query<Guid?>(
            "SELECT approval_request_id FROM ledger.ledger_transactions WHERE posting_key = @key", new { key = $"TX:{payment}:REFUND:{first}" }));

        var declined = await _h.ProcessingChild(payment, TransactionType.Refund, 60_000m);
        Ok(await _h.Finalization.ApplyAsync(new ProviderResultCommand(declined, AttemptResolutionKind.Failed, ChangeSource.SyncProviderResponse, "PROVIDER_DECLINED")));
        Assert.Equal((ProcessingStatus.PartiallyRefunded, FinancialStatus.PartiallyRefunded), await Status(payment));

        var rest = await _h.ProcessingChild(payment, TransactionType.Refund, 60_000m);
        Ok(await _h.Finalization.ApplyAsync(new ProviderResultCommand(rest, AttemptResolutionKind.Success, ChangeSource.SyncProviderResponse, "PROVIDER_SUCCESS")));
        var duplicate = await _h.Finalization.ApplyAsync(new ProviderResultCommand(rest, AttemptResolutionKind.Success, ChangeSource.Callback, "PROVIDER_SUCCESS"));

        Assert.Equal(TransitionKind.NoChange, duplicate.Value.Kind);
        Assert.Equal((ProcessingStatus.Refunded, FinancialStatus.Refunded), await Status(payment));
        Assert.Equal(61_500m, await JournalTotal($"TX:{payment}:REFUND:{rest}"));             // 60,000 + remaining 1,500
        Assert.Equal((1_000_000m, 1_000_000m, 0m), await Balances(wallet));
    }

    [Fact]
    public async Task Refund_with_fee_policy_none_returns_principal_only()
    {
        var (payment, wallet) = await PostedPayment(FeeRefundPolicy.None);
        var child = await _h.ProcessingChild(payment, TransactionType.Refund, 100_000m);

        Ok(await _h.Finalization.ApplyAsync(new ProviderResultCommand(child, AttemptResolutionKind.Success, ChangeSource.SyncProviderResponse, "PROVIDER_SUCCESS")));

        Assert.Equal((ProcessingStatus.Refunded, FinancialStatus.Refunded), await Status(payment));
        Assert.Equal(100_000m, await JournalTotal($"TX:{payment}:REFUND:{child}"));
        Assert.Equal((997_500m, 997_500m, 0m), await Balances(wallet));
    }

    [Fact]
    public async Task Confirmed_void_moves_no_money_and_flags_the_original_for_review()
    {
        var (payment, wallet) = await PostedPayment(FeeRefundPolicy.None);
        var child = await _h.ProcessingChild(payment, TransactionType.Void);

        Ok(await _h.Finalization.ApplyAsync(new ProviderResultCommand(child, AttemptResolutionKind.Success, ChangeSource.SyncProviderResponse, "PROVIDER_SUCCESS")));
        Ok(await _h.Finalization.ApplyAsync(new ProviderResultCommand(child, AttemptResolutionKind.Success, ChangeSource.Callback, "PROVIDER_SUCCESS")));

        var original = await _h.Load(payment);
        Assert.Equal((ProcessingStatus.Success, FinancialStatus.Posted, ReconciliationStatus.Exception),
            (original.ProcessingStatus, original.FinancialStatus, original.ReconciliationStatus));
        Assert.Equal(ReasonCodes.VoidConfirmedRequiresReview, await _h.Query<string>(
            "SELECT reason_code FROM core.transaction_state_history WHERE ransys_transaction_id = @id AND status_dimension = 'RECONCILIATION'",
            new { id = payment.Value }));
        Assert.Equal((ProcessingStatus.Success, FinancialStatus.None), await Status(child));
        Assert.Equal((897_500m, 897_500m, 0m), await Balances(wallet));
        Assert.Equal(0, await _h.Query<int>(
            "SELECT count(*)::int FROM ledger.ledger_transactions WHERE posting_key LIKE @a OR posting_key LIKE @b",
            new { a = $"TX:{payment}:REV%", b = $"TX:{payment}:REF%" }));
    }

    /// <summary>
    /// RR1 regression: the callback sink used to lock the child's row directly (<c>forUpdate: true</c>) before ever
    /// reaching finalization, which locks the parent before the child (ADR-012/023/019) — child → parent from the
    /// callback vs. parent → child from the sync completion path is a genuine PostgreSQL deadlock (40P01) when both
    /// race on the same child. The fix removes all locking from the sink: it only interprets the callback and calls
    /// <see cref="TransactionFinalizationService.ApplyAsync(IDatabaseSession, ProviderResultCommand, System.Threading.CancellationToken)"/>,
    /// the single lock-acquisition path both the callback and the sync completion path now share. Races the sink
    /// against a stand-in for the sync completion path (a direct <c>ApplyAsync</c> call with
    /// <see cref="ChangeSource.SyncProviderResponse"/>, the same call every sync-path test in this file already
    /// uses) over several concurrent refund/void children, each side opening its own session/connection, to force
    /// real lock contention. REVERSAL is not included: <see cref="CoreHarness.ProcessingChild"/> builds a generic
    /// child transaction directly (it does not go through <c>ReversalService</c>), which is enough to exercise
    /// REFUND and VOID finalization but not a reversal's provider-reference bookkeeping; REFUND and VOID already
    /// cover both of finalization's two post-child-success shapes (a ledger posting vs. none), which is what RR1's
    /// lock order fix actually changes.
    /// </summary>
    [Theory]
    [InlineData(TransactionType.Refund)]
    [InlineData(TransactionType.Void)]
    public async Task Callback_racing_the_sync_completion_path_on_a_child_never_deadlocks_and_finalizes_once(TransactionType childType)
    {
        var cases = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            var (payment, wallet) = await PostedPayment(FeeRefundPolicy.ProRata);
            var child = await _h.ProcessingChild(payment, childType, childType == TransactionType.Refund ? 40_000m : null);
            return (Payment: payment, Wallet: wallet, Child: child);
        }));

        var errors = new ConcurrentBag<Exception>();
        await Task.WhenAll(cases.SelectMany(c => new Task[]
        {
            Task.Run(async () =>
            {
                try
                {
                    await _h.CallbackSink.SubmitAsync(Callback(c.Child, ProviderOutcome.Success), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }),
            Task.Run(async () =>
            {
                try
                {
                    await _h.Finalization.ApplyAsync(
                        new ProviderResultCommand(c.Child, AttemptResolutionKind.Success, ChangeSource.SyncProviderResponse, "PROVIDER_SUCCESS"));
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }),
        }));

        Assert.Empty(errors); // no unhandled deadlock (40P01) or any other exception ever surfaces from either side

        foreach (var c in cases)
        {
            Assert.Equal(ProcessingStatus.Success, (await _h.Load(c.Child)).ProcessingStatus);
            if (childType == TransactionType.Refund)
            {
                Assert.Equal(1, await Journals($"TX:{c.Payment}:REFUND:{c.Child}")); // posted exactly once, never twice
                Assert.Equal((ProcessingStatus.PartiallyRefunded, FinancialStatus.PartiallyRefunded), await Status(c.Payment));
            }
            else
            {
                Assert.Equal(ReconciliationStatus.Exception, (await _h.Load(c.Payment)).ReconciliationStatus);
                Assert.Equal(0, await _h.Query<int>(
                    "SELECT count(*)::int FROM ledger.ledger_transactions WHERE posting_key LIKE @a", new { a = $"TX:{c.Payment}:REV%" }));
            }
        }
    }

    /// <summary>
    /// RR2 regression: once an attempt has ANY recorded outcome (here, a TIMEOUT-shaped callback that moved the
    /// transaction IN_DOUBT), the callback sink used to skip <c>RecordOutcomeAsync</c> entirely — correctly, per
    /// ADR-005 immutability — but then finalized a later, real SUCCESS callback's status/ledger effect while
    /// silently dropping its concrete provider reference/STAN/RRN, because nothing else persisted it. The fix adds
    /// <see cref="Transaction.LatestProviderResult"/> (ADR-027): an always-overwritable projection fed by callback
    /// evidence and read by GET, replay and child requests in preference to the (now possibly stale) per-attempt
    /// outcome.
    /// </summary>
    [Fact]
    public async Task Late_callback_after_a_recorded_timeout_still_persists_its_evidence_via_the_latest_result_projection()
    {
        var (payment, _) = await _h.ProcessingPayment();
        await _h.StartAttempt(payment, _h.ProviderA);

        // Step 1: an impossible (TIMEOUT-shaped) callback with no provider reference yet records that outcome on the
        // attempt and moves the transaction IN_DOUBT (see Impossible_callback_result_is_treated_as_in_doubt_and_keeps_the_hold
        // above; this test additionally drops the default provider reference to isolate "no evidence yet").
        var timedOut = await _h.CallbackSink.SubmitAsync(
            Callback(payment, ProviderOutcome.Success, transport: TransportStatus.Timeout, providerReference: null),
            CancellationToken.None);
        Assert.True(timedOut.Accepted);
        Assert.Equal(ProcessingStatus.InDoubt, (await _h.Load(payment)).ProcessingStatus);
        Assert.True(Assert.Single(await _h.LoadAttempts(payment)).IsOutcomeRecorded);

        // Step 2: the real, final SUCCESS callback with concrete provider evidence arrives afterwards.
        var resolved = await _h.CallbackSink.SubmitAsync(
            Callback(payment, ProviderOutcome.Success, providerReference: "PRV-REF-999", providerStan: "123456", providerRrn: "RRN000111"),
            CancellationToken.None);
        Assert.True(resolved.Accepted);

        // Reload in a fresh session, as GET / a child request would.
        var reloaded = await _h.Load(payment);
        Assert.Equal(ProcessingStatus.Success, reloaded.ProcessingStatus);
        Assert.NotNull(reloaded.LatestProviderResult);
        Assert.Equal(ChangeSource.Callback, reloaded.LatestProviderResult!.Source);
        Assert.Equal(
            ("PRV-REF-999", "123456", "RRN000111"),
            (reloaded.LatestProviderResult.Evidence.ProviderReference,
             reloaded.LatestProviderResult.Evidence.ProviderStan,
             reloaded.LatestProviderResult.Evidence.ProviderRrn));

        // The attempt's own recorded outcome is untouched (still the earlier TIMEOUT-shaped one, ADR-005): only the
        // transaction-level projection carries the later report.
        var attemptAfterwards = Assert.Single(await _h.LoadAttempts(payment));
        Assert.Null(attemptAfterwards.Outcome!.ProviderReference);

        // A refund/reversal/void child request for this original now sees the late evidence, never the stale one.
        var forChild = OriginalProviderReferences.From(reloaded, await _h.LoadAttempts(payment));
        Assert.Equal(("PRV-REF-999", "123456", "RRN000111"), (forChild.ProviderReference, forChild.ProviderStan, forChild.ProviderRrn));

        // GET's read path prefers it too.
        await using var session = await _h.Session();
        var queryRow = await new PostgresTransactionQuery().FindForChannelAsync(session, new ChannelId(db.Seed.ChannelId), payment);
        Assert.NotNull(queryRow);
        Assert.Equal(("123456", "RRN000111"), (queryRow!.Stan, queryRow.Rrn));
    }

    private async Task<(TransactionId Payment, WalletId Wallet)> PostedPayment(FeeRefundPolicy policy)
    {
        var (payment, wallet) = await _h.ProcessingPayment(feePolicy: policy);
        Ok(await _h.Finalization.ApplyAsync(new ProviderResultCommand(payment, AttemptResolutionKind.Success, ChangeSource.SyncProviderResponse, "PROVIDER_SUCCESS")));
        return (payment, wallet);
    }

    private ProviderCallback Callback(
        TransactionId transaction,
        ProviderOutcome outcome,
        ResultFinality finality = ResultFinality.Definitive,
        TransportStatus transport = TransportStatus.Response,
        bool requestSent = true,
        string? providerReference = "PRV-CB",
        string? providerStan = null,
        string? providerRrn = null)
    {
        var references = new ProviderTransactionReferences("REF", null, null, null, providerReference, providerStan, providerRrn, ImmutableDictionary<string, string>.Empty);
        var result = new ProviderResult(
            outcome, finality, new ProviderTransportResult(transport, requestSent, null, null, null),
            outcome == ProviderOutcome.Success ? "0000" : "1001", "00", "callback", references,
            ImmutableDictionary<string, JsonElement>.Empty, null, DateTimeOffset.UtcNow, null, null);
        return new ProviderCallback(
            db.Seed.ProviderAId, $"CB-{Guid.NewGuid():N}", transaction.Value, "PRV-CB", result,
            new CorrelationContext(transaction.Value, transaction.ToString(), "trace"), DateTimeOffset.UtcNow);
    }

    private async Task<(ProcessingStatus, FinancialStatus)> Status(TransactionId id)
    {
        var t = await _h.Load(id);
        return (t.ProcessingStatus, t.FinancialStatus);
    }

    private Task<int> Journals(string key) =>
        _h.Query<int>("SELECT count(*)::int FROM ledger.ledger_transactions WHERE posting_key = @key", new { key });

    private Task<decimal> JournalTotal(string key) =>
        _h.Query<decimal>(
            """
            SELECT COALESCE(sum(e.amount), 0) FROM ledger.ledger_entries e
            JOIN ledger.ledger_transactions j ON j.ledger_transaction_id = e.ledger_transaction_id
            WHERE j.posting_key = @key AND e.entry_side = 'C'
            """,
            new { key });

    private async Task<(decimal Ledger, decimal Available, decimal Reserved)> Balances(WalletId wallet)
    {
        var row = await _h.Query<(decimal, decimal, decimal)>(
            "SELECT ledger_balance, available_balance, reserved_balance FROM ledger.wallets WHERE wallet_id = @id", new { id = wallet.Value });
        return row;
    }
}
