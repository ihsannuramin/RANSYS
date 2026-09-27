using System.Collections.Immutable;
using System.Text.Json;
using Ransys.Adapter.Contracts.V1;
using Ransys.Domain;
using Ransys.Domain.Attempts;
using Ransys.Domain.Common;
using Ransys.Domain.Fees;
using Ransys.Domain.Transactions;
using Ransys.IntegrationTests.Processing;
using Ransys.Persistence.PostgreSql.Queries;
using Ransys.Testing;
using Ransys.Testing.PostgreSql;
using Ransys.TransactionCore.Finalization;
using Ransys.TransactionCore.Processing;
using Ransys.TransactionCore.Providers;
using Ransys.TransactionCore.Reversal;
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

    // T2-B/T3 need the full TransactionProcessingService (real sync-path interleaving with a scripted adapter),
    // not just CoreHarness's direct finalization/callback plumbing.
    private readonly ProcessingHarness _p = new(db);

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
    /// real lock contention.
    /// <para>
    /// T4 (b9616fb): the previous version of this test only checked that no exception surfaced and asserted the
    /// winning side's final state — but <see cref="ProviderCallbackSink.SubmitAsync"/> catches a real
    /// <see cref="System.Data.Common.DbException"/> (which includes a Postgres deadlock, SQLSTATE 40P01) internally
    /// and returns a normal <c>Accepted = false</c> ack, never a thrown exception. A real deadlock absorbed this way
    /// would have passed silently. This version asserts BOTH sides' actual results explicitly (the callback's
    /// <see cref="ProviderCallbackAck.Accepted"/> and the sync completion's <see cref="Result{T}.IsSuccess"/>), with a
    /// bounded wait per operation so a genuine hang fails clearly instead of blocking the suite. A REVERSAL variant
    /// follows separately below, since it needs a real <see cref="ReversalService"/>-created child (its own
    /// provider-reference bookkeeping, ADR-012) rather than <see cref="CoreHarness.ProcessingChild"/>'s generic child;
    /// REFUND and VOID here already cover both of finalization's two post-child-success shapes (a ledger posting vs.
    /// none), which is what RR1's lock order fix actually changes.
    /// </para>
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

        var races = await Task.WhenAll(cases.Select(async c =>
        {
            var callbackTask = Task.Run(() => _h.CallbackSink.SubmitAsync(Callback(c.Child, ProviderOutcome.Success), CancellationToken.None));
            var syncTask = Task.Run(() => _h.Finalization.ApplyAsync(
                new ProviderResultCommand(c.Child, AttemptResolutionKind.Success, ChangeSource.SyncProviderResponse, "PROVIDER_SUCCESS")));
            var callback = await Bounded(callbackTask, $"callback race for child {c.Child}");
            var sync = await Bounded(syncTask, $"sync completion race for child {c.Child}");
            return (c.Payment, c.Wallet, c.Child, Callback: callback, Sync: sync);
        }));

        foreach (var r in races)
        {
            // Neither side may silently fail-closed (no unhandled deadlock (40P01) either): both must reach a real outcome.
            Assert.True(r.Callback.Accepted, $"callback for child {r.Child} was not accepted: {r.Callback.AcknowledgementCode}");
            Assert.True(r.Sync.IsSuccess, $"sync completion for child {r.Child} failed: {(r.Sync.IsFailure ? r.Sync.Error.ToString() : string.Empty)}");

            Assert.Equal(ProcessingStatus.Success, (await _h.Load(r.Child)).ProcessingStatus);
            if (childType == TransactionType.Refund)
            {
                Assert.Equal(1, await Journals($"TX:{r.Payment}:REFUND:{r.Child}")); // posted exactly once, never twice
                Assert.Equal((ProcessingStatus.PartiallyRefunded, FinancialStatus.PartiallyRefunded), await Status(r.Payment));
            }
            else
            {
                Assert.Equal(ReconciliationStatus.Exception, (await _h.Load(r.Payment)).ReconciliationStatus);
                Assert.Equal(0, await _h.Query<int>(
                    "SELECT count(*)::int FROM ledger.ledger_transactions WHERE posting_key LIKE @a", new { a = $"TX:{r.Payment}:REV%" }));
            }
        }
    }

    /// <summary>
    /// T4 REVERSAL variant (b9616fb): the REFUND/VOID cases above build a generic child transaction directly
    /// (<see cref="CoreHarness.ProcessingChild"/>); this drives the same callback-vs-sync-completion race through a
    /// real REVERSAL child created by <see cref="ReversalService.StartAsync"/>, so the child's own provider-reference
    /// bookkeeping and <see cref="TransactionFinalizationService"/>'s <c>ApplyReversalToOriginalAsync</c> path
    /// (ADR-012) are exercised under the same parent → child lock order, not only REFUND/VOID's.
    /// </summary>
    [Fact]
    public async Task Callback_racing_the_sync_completion_path_on_a_reversal_child_never_deadlocks_and_finalizes_once()
    {
        await _h.GrantCapability(_h.ProviderA.ProviderId, Ransys.Domain.Routing.ProviderCapabilities.Reversal);

        var cases = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            var (payment, wallet) = await PostedPayment(FeeRefundPolicy.None);

            TransactionId child;
            await using (var session = await _h.Session())
            {
                var started = Ok(await _h.Reversals.StartAsync(
                    session, new StartReversalCommand(payment, $"REV-{Guid.NewGuid():N}", null, "REVERSAL_REQUESTED", ChangeSource.ManualAction)));
                await session.CommitAsync();
                child = started.ReversalTransactionId;
            }

            await using (var attemptSession = await _h.Session())
            {
                var transaction = Ok(await _h.Transactions.GetAsync(attemptSession, child, Persistence.PostgreSql.Transactions.RowLock.ForUpdate))!;
                Ok(await _h.Attempts.StartAsync(attemptSession, transaction, AttemptType.Reversal, transaction.Routing!.CurrentProvider, "corr-rev", "trace-rev"));
                await attemptSession.CommitAsync();
            }

            return (Payment: payment, Wallet: wallet, Child: child);
        }));

        var races = await Task.WhenAll(cases.Select(async c =>
        {
            var callbackTask = Task.Run(() => _h.CallbackSink.SubmitAsync(Callback(c.Child, ProviderOutcome.Success), CancellationToken.None));
            var syncTask = Task.Run(() => _h.Finalization.ApplyAsync(
                new ProviderResultCommand(c.Child, AttemptResolutionKind.Success, ChangeSource.SyncProviderResponse, "REVERSAL_CONFIRMED")));
            var callback = await Bounded(callbackTask, $"callback race for reversal child {c.Child}");
            var sync = await Bounded(syncTask, $"sync completion race for reversal child {c.Child}");
            return (c.Payment, c.Wallet, c.Child, Callback: callback, Sync: sync);
        }));

        foreach (var r in races)
        {
            Assert.True(r.Callback.Accepted, $"callback for reversal child {r.Child} was not accepted: {r.Callback.AcknowledgementCode}");
            Assert.True(r.Sync.IsSuccess, $"sync completion for reversal child {r.Child} failed: {(r.Sync.IsFailure ? r.Sync.Error.ToString() : string.Empty)}");

            Assert.Equal(ProcessingStatus.Success, (await _h.Load(r.Child)).ProcessingStatus);
            Assert.Equal((ProcessingStatus.Reversed, FinancialStatus.Reversed), await Status(r.Payment));
            Assert.Equal(1, await Journals($"TX:{r.Payment}:REVERSAL:{r.Child}")); // posted exactly once, never twice
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

    /// <summary>
    /// T1 (external re-review, <c>review/RANSYS_Architecture_Review_b9616fb.md</c>): a callback that CONTRADICTS the
    /// already-accepted result (e.g. FAILED arriving after an accepted SUCCESS/POSTED) produces
    /// <see cref="TransitionKind.ConflictRecorded"/>, never <see cref="TransitionKind.Applied"/>. Its evidence must
    /// never overwrite <see cref="Transaction.LatestProviderResult"/>: promoting a contradicting report to "the"
    /// evidence would let GET/replay/child requests read the wrong reference/RRN/data for a transaction whose real,
    /// accepted outcome never changed. The conflict itself stays fully visible via the reconciliation exception this
    /// transition already records.
    /// </summary>
    [Fact]
    public async Task Conflicting_callback_never_overwrites_the_accepted_evidence_projection()
    {
        var (payment, _) = await _h.ProcessingPayment();
        await _h.StartAttempt(payment, _h.ProviderA);

        var dataA = DataOf("field", "A");
        var accepted = await _h.CallbackSink.SubmitAsync(
            Callback(payment, ProviderOutcome.Success, providerReference: "PRV-A", providerStan: "STAN-A", providerRrn: "RRN-A", data: dataA),
            CancellationToken.None);
        Assert.Equal((true, ProviderCallbackSink.Applied), (accepted.Accepted, accepted.AcknowledgementCode));

        // Sub-case 1: a conflicting FAILED report with its own concrete but different reference/RRN/data.
        var dataB = DataOf("field", "B");
        var conflict = await _h.CallbackSink.SubmitAsync(
            Callback(payment, ProviderOutcome.Failed, providerReference: "PRV-B", providerStan: "STAN-B", providerRrn: "RRN-B", data: dataB),
            CancellationToken.None);
        Assert.Equal((true, ProviderCallbackSink.ConflictRecorded), (conflict.Accepted, conflict.AcknowledgementCode));
        await AssertAcceptedEvidenceUnchanged(payment);
        Assert.Equal(1, await Journals($"TX:{payment}:POST"));

        // Sub-case 2: a conflicting FAILED report with NO reference/data at all must not clear the accepted projection.
        var blankConflict = await _h.CallbackSink.SubmitAsync(
            Callback(payment, ProviderOutcome.Failed, providerReference: null, providerStan: null, providerRrn: null, data: null),
            CancellationToken.None);
        Assert.Equal((true, ProviderCallbackSink.ConflictRecorded), (blankConflict.Accepted, blankConflict.AcknowledgementCode));
        await AssertAcceptedEvidenceUnchanged(payment);

        // Sub-case 3: repeating the SAME conflicting FAILED callback again must still leave the accepted evidence untouched.
        var repeated = await _h.CallbackSink.SubmitAsync(
            Callback(payment, ProviderOutcome.Failed, providerReference: "PRV-B", providerStan: "STAN-B", providerRrn: "RRN-B", data: dataB),
            CancellationToken.None);
        Assert.True(repeated.Accepted);
        await AssertAcceptedEvidenceUnchanged(payment);
        Assert.Equal(1, await Journals($"TX:{payment}:POST")); // still exactly one posting throughout

        async Task AssertAcceptedEvidenceUnchanged(TransactionId id)
        {
            var reloaded = await _h.Load(id);
            Assert.Equal((ProcessingStatus.Success, FinancialStatus.Posted, ReconciliationStatus.Exception),
                (reloaded.ProcessingStatus, reloaded.FinancialStatus, reloaded.ReconciliationStatus));
            Assert.NotNull(reloaded.LatestProviderResult);
            Assert.Equal(ChangeSource.Callback, reloaded.LatestProviderResult!.Source);
            Assert.Equal(
                ("PRV-A", "STAN-A", "RRN-A"),
                (reloaded.LatestProviderResult.Evidence.ProviderReference,
                 reloaded.LatestProviderResult.Evidence.ProviderStan,
                 reloaded.LatestProviderResult.Evidence.ProviderRrn));
            Assert.Equal("A", FieldOf(reloaded.LatestProviderResult.Evidence.Data, "field"));
        }
    }

    /// <summary>
    /// T2-A (b9616fb): the sync path already completed the transaction to SUCCESS with minimal evidence (no
    /// reference/RRN captured — the fix in <see cref="TransactionProcessingService.RecordInSessionAsync"/> did not
    /// exist yet at that point of this scenario). A later callback reports the SAME already-accepted SUCCESS
    /// (<see cref="TransitionKind.NoChange"/>) but with a fuller reference/RRN/data payload. Before the fix,
    /// finalization returned before ever reaching the evidence write for <c>NoChange</c>, so this richer evidence was
    /// silently dropped forever. No status/ledger/outbox effect should follow — only the projection enrichment.
    /// </summary>
    [Fact]
    public async Task Callback_success_after_an_already_accepted_sync_success_still_enriches_the_projection()
    {
        var (payment, wallet) = await _h.ProcessingPayment();
        await _h.StartAttempt(payment, _h.ProviderA);

        Ok(await _h.Finalization.ApplyAsync(new ProviderResultCommand(payment, AttemptResolutionKind.Success, ChangeSource.SyncProviderResponse, "PROVIDER_SUCCESS")));
        Assert.Equal((ProcessingStatus.Success, FinancialStatus.Posted), await Status(payment));
        Assert.Null((await _h.Load(payment)).LatestProviderResult); // the sync path here passed no Evidence

        var eventsBefore = await StatusEventCount(payment);
        var dataFull = DataOf("field", "full");

        var callback = await _h.CallbackSink.SubmitAsync(
            Callback(payment, ProviderOutcome.Success, providerReference: "PRV-FULL", providerStan: "STAN-FULL", providerRrn: "RRN-FULL", data: dataFull),
            CancellationToken.None);

        Assert.Equal((true, ProviderCallbackSink.Duplicate), (callback.Accepted, callback.AcknowledgementCode));
        var reloaded = await _h.Load(payment);
        Assert.Equal((ProcessingStatus.Success, FinancialStatus.Posted), (reloaded.ProcessingStatus, reloaded.FinancialStatus));
        Assert.NotNull(reloaded.LatestProviderResult);
        Assert.Equal(ChangeSource.Callback, reloaded.LatestProviderResult!.Source);
        Assert.Equal(
            ("PRV-FULL", "STAN-FULL", "RRN-FULL"),
            (reloaded.LatestProviderResult.Evidence.ProviderReference,
             reloaded.LatestProviderResult.Evidence.ProviderStan,
             reloaded.LatestProviderResult.Evidence.ProviderRrn));
        Assert.Equal("full", FieldOf(reloaded.LatestProviderResult.Evidence.Data, "field"));
        Assert.Equal(1, await Journals($"TX:{payment}:POST")); // no new posting from the NoChange-kind callback
        Assert.Equal((897_500m, 897_500m, 0m), await Balances(wallet));
        Assert.Equal(eventsBefore, await StatusEventCount(payment)); // no new/duplicate outbox status event
    }

    /// <summary>
    /// T2-B (b9616fb): a callback PENDING arrives first, with temporary reference/data, while the primary attempt is
    /// still in flight at the provider — it records that outcome on the attempt (ADR-005: the attempt had no
    /// recorded outcome yet) and moves the transaction to PENDING. The SAME attempt's sync completion path then
    /// resolves it to SUCCESS with its OWN, different, more final evidence
    /// (<see cref="TransactionProcessingService.RecordInSessionAsync"/> now passes <c>Evidence</c> too). The real
    /// interleaving is forced by submitting the callback from inside the scripted adapter's own response step, i.e.
    /// while <see cref="TransactionProcessingService.PayAsync"/> is still awaiting the provider call.
    /// </summary>
    [Fact]
    public async Task Sync_success_after_a_stale_pending_callback_projects_its_own_final_evidence()
    {
        var s = await _p.NewScenario();
        var providerId = s.Providers[0].Id;
        var command = Payment(s, 100_000m);

        s.Adapter.Then(async (_, request, _) =>
        {
            var ack = await _p.Core.CallbackSink.SubmitAsync(
                Callback(
                    new TransactionId(request.RansysTransactionId), ProviderOutcome.Pending, ResultFinality.NonFinal,
                    providerReference: "PRV-TEMP", providerStan: "STAN-TEMP", providerRrn: null, data: DataOf("field", "temp"),
                    providerId: providerId.Value),
                CancellationToken.None);
            Assert.True(ack.Accepted);
            return ScriptedProviderAdapter.Success(request, providerReference: "PRV-FINAL", rrn: "RRN-FINAL");
        });

        var result = Ok(await _p.Service.PayAsync(command));

        Assert.Equal(ProcessingStatus.Success, result.ProcessingStatus);
        Assert.Equal("RRN-FINAL", result.References.Rrn); // the immediate sync response already carries the final evidence

        var reloaded = await _p.Core.Load(result.TransactionId);
        Assert.NotNull(reloaded.LatestProviderResult);
        Assert.Equal(ChangeSource.SyncProviderResponse, reloaded.LatestProviderResult!.Source);
        Assert.Equal(
            ("PRV-FINAL", "RRN-FINAL"),
            (reloaded.LatestProviderResult.Evidence.ProviderReference, reloaded.LatestProviderResult.Evidence.ProviderRrn));

        // ADR-005: the attempt's own recorded outcome stays the earlier PENDING one; only the projection carries the final report.
        var attempt = Assert.Single(await _p.Core.LoadAttempts(result.TransactionId));
        Assert.Equal("PRV-TEMP", attempt.Outcome!.ProviderReference);

        // A replay of the same request must see the final evidence, never the stale PENDING one.
        var replay = Ok(await _p.Service.PayAsync(command with { RequestTimestamp = DateTimeOffset.UtcNow.AddSeconds(5) }));
        Assert.True(replay.IsReplay);
        Assert.Equal("RRN-FINAL", replay.References.Rrn);
    }

    /// <summary>
    /// T3 (b9616fb): the primary attempt times out with no RRN captured (transaction IN_DOUBT). A callback SUCCESS
    /// then arrives with a concrete STAN/RRN. GET already preferred the projection (ADR-027); this asserts that a
    /// REPLAY of the same original merchant request — the actual public POST, via
    /// <see cref="TransactionProcessingService"/>, not only a query — shows the same references, and that the
    /// provider adapter was invoked exactly once total across the original attempt and the replay.
    /// </summary>
    [Fact]
    public async Task Replay_after_a_late_callback_shows_the_same_references_as_get_and_calls_the_provider_only_once()
    {
        var s = await _p.NewScenario();
        s.Adapter.ThenHang(); // times out within the Core budget: IN_DOUBT, RequestSent=true, no RRN captured
        var command = Payment(s, 100_000m);

        var first = Ok(await _p.Service.PayAsync(command));
        Assert.Equal(ProcessingStatus.InDoubt, first.ProcessingStatus);
        Assert.Null(first.References.Rrn);

        var callback = await _p.Core.CallbackSink.SubmitAsync(
            Callback(
                first.TransactionId, ProviderOutcome.Success,
                providerReference: "PRV-LATE", providerStan: "STAN-LATE", providerRrn: "RRN-LATE", providerId: s.Providers[0].Id.Value),
            CancellationToken.None);
        Assert.True(callback.Accepted);

        var detail = await _p.Service.GetTransactionAsync(s.Channel, first.TransactionId);
        Assert.Equal(("STAN-LATE", "RRN-LATE"), (detail!.References.Stan, detail.References.Rrn));

        var replay = Ok(await _p.Service.PayAsync(command with { RequestTimestamp = DateTimeOffset.UtcNow.AddSeconds(5) }));

        Assert.True(replay.IsReplay);
        Assert.Equal(first.TransactionId, replay.TransactionId);
        Assert.Equal(("STAN-LATE", "RRN-LATE"), (replay.References.Stan, replay.References.Rrn));
        Assert.Equal(1, s.Adapter.CallCount); // the provider was invoked exactly once total; the replay makes no new call
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
        string? providerRrn = null,
        IReadOnlyDictionary<string, JsonElement>? data = null,
        Guid? providerId = null)
    {
        var references = new ProviderTransactionReferences("REF", null, null, null, providerReference, providerStan, providerRrn, ImmutableDictionary<string, string>.Empty);
        var result = new ProviderResult(
            outcome, finality, new ProviderTransportResult(transport, requestSent, null, null, null),
            outcome == ProviderOutcome.Success ? "0000" : "1001", "00", "callback", references,
            data ?? ImmutableDictionary<string, JsonElement>.Empty, null, DateTimeOffset.UtcNow, null, null);
        return new ProviderCallback(
            providerId ?? db.Seed.ProviderAId, $"CB-{Guid.NewGuid():N}", transaction.Value, "PRV-CB", result,
            new CorrelationContext(transaction.Value, transaction.ToString(), "trace"), DateTimeOffset.UtcNow);
    }

    /// <summary>A scenario-scoped payment command (<see cref="ProcessingHarness"/>), for T2-B/T3's real service interleaving.</summary>
    private static PaymentCommand Payment(Scenario s, decimal amount) =>
        new(s.Channel, s.Merchant, $"REF-{Guid.CreateVersion7():N}", null, s.ProductCode, new MoneyInput(amount, "IDR"),
            new CustomerInput(CustomerId: "CUST-1"), null, new EndpointInput(EndpointType.Biller, "PLN-1"), null, DateTimeOffset.UtcNow);

    private static IReadOnlyDictionary<string, JsonElement> DataOf(string field, string value) =>
        ImmutableDictionary<string, JsonElement>.Empty.Add(field, JsonSerializer.SerializeToElement(value));

    private static string? FieldOf(IReadOnlyDictionary<string, JsonElement>? data, string field) =>
        data is not null && data.TryGetValue(field, out var value) ? value.GetString() : null;

    /// <summary>Runs a task with a generous but finite timeout, so a genuine hang fails clearly instead of blocking the suite.</summary>
    private static async Task<T> Bounded<T>(Task<T> task, string label, TimeSpan? timeout = null)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout ?? TimeSpan.FromSeconds(30)));
        if (!ReferenceEquals(completed, task))
        {
            throw new TimeoutException($"{label} did not complete within the bounded wait.");
        }

        return await task;
    }

    private async Task<(ProcessingStatus, FinancialStatus)> Status(TransactionId id)
    {
        var t = await _h.Load(id);
        return (t.ProcessingStatus, t.FinancialStatus);
    }

    private Task<int> Journals(string key) =>
        _h.Query<int>("SELECT count(*)::int FROM ledger.ledger_transactions WHERE posting_key = @key", new { key });

    private Task<int> StatusEventCount(TransactionId id) =>
        _h.Query<int>(
            "SELECT count(*)::int FROM async.outbox_events WHERE aggregate_id = @id AND aggregate_type = 'TRANSACTION'",
            new { id = id.Value });

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
