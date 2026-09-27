using Ransys.Domain.Attempts;
using Ransys.Domain.Common;
using Ransys.Domain.Transactions;
using Ransys.Ledger;
using Ransys.Testing.PostgreSql;
using Ransys.TransactionCore.Attempts;
using static Ransys.IntegrationTests.CoreHarness;

namespace Ransys.IntegrationTests.Attempts;

/// <summary>
/// Transaction attempts on a real PostgreSQL server (main.md §17, ADR-005, State Transition Matrix §15, §64–65, §72.20).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TransactionAttemptTests(PostgresDatabaseFixture db)
{
    private readonly CoreHarness _h = new(db);

    [Fact]
    public async Task Started_attempt_is_persisted_as_possibly_sent()
    {
        var (tx, _) = await _h.ProcessingPayment();

        var attempt = await StartPayment(tx, _h.ProviderA);

        var row = await _h.Query<(bool RequestSent, string TransportStatus, DateTime? RecordedAt, int AttemptNo)>(
            """
            SELECT request_sent, transport_status, outcome_recorded_at, attempt_no
            FROM core.transaction_attempts WHERE transaction_attempt_id = @id
            """,
            new { id = attempt.Id.Value });
        Assert.Equal((true, "SENT", (DateTime?)null, 1), row);

        var loaded = Assert.Single(await _h.LoadAttempts(tx));
        Assert.False(loaded.IsOutcomeRecorded);
        Assert.True(loaded.MayHaveReachedProvider);
    }

    [Fact]
    public async Task Not_sent_outcome_allows_failover_and_numbering_continues_on_the_next_provider()
    {
        var (tx, _) = await _h.ProcessingPayment();
        var first = await StartPayment(tx, _h.ProviderA);
        await Record(first, AttemptOutcome.Create(false, TransportStatus.ConnectionError, latency: TimeSpan.FromMilliseconds(12)).Value);

        await using (var session = await _h.Session())
        {
            var transaction = Ok(await _h.Transactions.GetAsync(session, tx, Persistence.PostgreSql.Transactions.RowLock.ForUpdate))!;
            var attempts = Ok(await _h.AttemptStore.GetByTransactionAsync(session, tx));
            Assert.True(transaction.RecordFailover(attempts[0], _h.ProviderB, "PROVIDER_LINK_DOWN", DateTimeOffset.UtcNow).IsSuccess);
            var second = Ok(await _h.Attempts.StartAsync(session, transaction, AttemptType.Payment, _h.ProviderB, "corr", "trace"));
            Ok(await _h.Transactions.UpdateAsync(session, transaction));
            await session.CommitAsync();
            Assert.Equal(2, second.AttemptNumber);
        }

        var loaded = await _h.LoadAttempts(tx);
        Assert.True(loaded[0].ProvesRequestNotSent);
        Assert.Equal(TimeSpan.FromMilliseconds(12), loaded[0].Outcome!.Latency);
        Assert.Equal(_h.ProviderB, loaded[1].Provider);
        Assert.Equal(ProcessingStatus.Processing, (await _h.Load(tx)).ProcessingStatus); // §65: stays PROCESSING
    }

    [Fact]
    public async Task Outcome_is_recorded_once()
    {
        var (tx, _) = await _h.ProcessingPayment();
        var attempt = await StartPayment(tx, _h.ProviderA);
        var timeout = AttemptOutcome.Create(true, TransportStatus.Timeout, providerSentAt: DateTimeOffset.UtcNow).Value;
        await Record(attempt, timeout);

        // A second path (e.g. a late callback handler) loaded the attempt before the outcome was stored.
        var stale = Domain.Attempts.TransactionAttempt.Rehydrate(
            attempt.Id, attempt.TransactionId, attempt.AttemptNumber, attempt.AttemptType, attempt.Provider,
            attempt.CorrelationId, attempt.TraceId, attempt.CreatedAt, outcome: null).Value;
        await using var session = await _h.Session();
        var result = await _h.Attempts.RecordOutcomeAsync(
            session, stale, AttemptOutcome.Create(true, TransportStatus.Response, providerResponseCode: "00").Value);

        Assert.Equal(ErrorCodes.AttemptOutcomeAlreadyRecorded, result.Error.Code);
        Assert.Equal(TransportStatus.Timeout, Assert.Single(await _h.LoadAttempts(tx)).Outcome!.TransportStatus);
    }

    [Fact]
    public async Task Timeout_goes_in_doubt_with_hold_kept_and_blocks_any_new_financial_attempt()
    {
        var (tx, wallet) = await _h.ProcessingPayment();
        var attempt = await StartPayment(tx, _h.ProviderA);

        await using (var session = await _h.Session())
        {
            var transaction = Ok(await _h.Transactions.GetAsync(session, tx, Persistence.PostgreSql.Transactions.RowLock.ForUpdate))!;
            var outcome = AttemptOutcome.Create(true, TransportStatus.Timeout, providerSentAt: DateTimeOffset.UtcNow).Value;
            Ok(await _h.Attempts.RecordOutcomeAsync(session, attempt, outcome));

            Assert.Equal(AttemptResolutionKind.InDoubt, AttemptResolution.Classify(attempt.Outcome, providerOutcome: null));
            Ok(transaction.MarkInDoubt(Ctx("PROVIDER_READ_TIMEOUT", ChangeSource.SyncProviderResponse, attempt.Id)));
            Ok(await _h.Ledger.ChangeHoldReasonAsync(session, tx, "IN_DOUBT"));
            Ok(await _h.Transactions.UpdateAsync(session, transaction));
            await session.CommitAsync();
        }

        var after = await _h.Load(tx);
        Assert.Equal((ProcessingStatus.InDoubt, FinancialStatus.Reserved), (after.ProcessingStatus, after.FinancialStatus));
        Assert.Equal(("ACTIVE", "IN_DOUBT"), await _h.Query<(string, string)>(
            "SELECT status, hold_reason FROM ledger.balance_reservations WHERE ransys_transaction_id = @id", new { id = tx.Value }));
        Assert.Equal(897_500m, await _h.Query<decimal>(
            "SELECT available_balance FROM ledger.wallets WHERE wallet_id = @id", new { id = wallet.Value }));
        Assert.Equal(1, await _h.Query<int>(
            "SELECT count(*) FROM core.transaction_state_history WHERE transaction_attempt_id = @id", new { id = attempt.Id.Value }));

        await using var retry = await _h.Session();
        var locked = Ok(await _h.Transactions.GetAsync(retry, tx, Persistence.PostgreSql.Transactions.RowLock.ForUpdate))!;
        Assert.Equal(ErrorCodes.AttemptNotAllowed,
            (await _h.Attempts.StartAsync(retry, locked, AttemptType.Payment, _h.ProviderA, "c", "t")).Error.Code);
        Assert.Equal(ErrorCodes.AttemptNotAllowed,
            (await _h.Attempts.StartAsync(retry, locked, AttemptType.Payment, _h.ProviderB, "c", "t")).Error.Code);
        Assert.True((await _h.Attempts.StartAsync(retry, locked, AttemptType.StatusCheck, _h.ProviderA, "c", "t")).IsSuccess);
    }

    [Fact]
    public async Task Crash_during_provider_call_is_recovered_as_in_doubt_never_failover()
    {
        // State Transition Matrix §72.20: Transaction Core restarts while the provider call is in flight.
        var (tx, _) = await _h.ProcessingPayment();
        var attempt = await StartPayment(tx, _h.ProviderA); // committed, then the process "crashes"

        await using (var session = await _h.Session())
        {
            var candidates = await _h.Recovery.FindCandidatesAsync(session, TimeSpan.Zero, 1000);
            Assert.Contains((attempt.Id, tx), candidates);

            var result = await _h.Recovery.RecoverAsync(session, tx, attempt.Id);
            Assert.Equal(AttemptRecoveryOutcome.MarkedInDoubt, result.Value);
            await session.CommitAsync();
        }

        var after = await _h.Load(tx);
        Assert.Equal((ProcessingStatus.InDoubt, FinancialStatus.Reserved), (after.ProcessingStatus, after.FinancialStatus));
        Assert.Equal(ReasonCodes.AttemptOutcomeUnknown, after.ReasonCode);

        var recovered = Assert.Single(await _h.LoadAttempts(tx));
        Assert.True(recovered.IsOutcomeRecorded);
        Assert.True(recovered.MayHaveReachedProvider);
        Assert.Equal((true, TransportStatus.Timeout), (recovered.Outcome!.RequestSent, recovered.Outcome.TransportStatus));
        Assert.Equal("SYSTEM_RECOVERY", recovered.Outcome.Metadata.Values[AttemptRecoveryService.OutcomeSourceKey].GetString());
        Assert.Equal("IN_DOUBT", await _h.Query<string>(
            "SELECT hold_reason FROM ledger.balance_reservations WHERE ransys_transaction_id = @id", new { id = tx.Value }));

        await using var again = await _h.Session();
        Assert.DoesNotContain((attempt.Id, tx), await _h.Recovery.FindCandidatesAsync(again, TimeSpan.Zero, 1000));
        Assert.Equal(AttemptRecoveryOutcome.AlreadyResolved, (await _h.Recovery.RecoverAsync(again, tx, attempt.Id)).Value);
    }

    /// <summary>
    /// R5 (architecture review finding): recovery must emit the same TRANSACTION status event finalization emits for
    /// every other path to IN_DOUBT, so Backoffice replication and downstream consumers see the recovered state too.
    /// </summary>
    [Fact]
    public async Task Recovery_enqueues_the_in_doubt_status_event_exactly_once()
    {
        var (tx, _) = await _h.ProcessingPayment();
        var attempt = await StartPayment(tx, _h.ProviderA);

        await using (var session = await _h.Session())
        {
            var result = await _h.Recovery.RecoverAsync(session, tx, attempt.Id);
            Assert.Equal(AttemptRecoveryOutcome.MarkedInDoubt, result.Value);
            await session.CommitAsync();
        }

        var after = await _h.Load(tx);
        Assert.Equal((ProcessingStatus.InDoubt, FinancialStatus.Reserved), (after.ProcessingStatus, after.FinancialStatus));
        Assert.Equal("IN_DOUBT", await _h.Query<string>(
            "SELECT hold_reason FROM ledger.balance_reservations WHERE ransys_transaction_id = @id", new { id = tx.Value }));

        Assert.Equal(1, await EventCount(tx, "TRANSACTION_IN_DOUBT"));
        var (sourceVersion, rowVersion) = await _h.Query<(long, long)>(
            """
            SELECT
              (SELECT source_version FROM async.outbox_events WHERE aggregate_id = @id AND event_type = 'TRANSACTION_IN_DOUBT'),
              (SELECT row_version FROM core.transactions WHERE ransys_transaction_id = @id)
            """,
            new { id = tx.Value });
        Assert.Equal(rowVersion, sourceVersion);

        // A second recovery of the very same (now resolved) attempt must not emit a duplicate event.
        await using var again = await _h.Session();
        Assert.Equal(AttemptRecoveryOutcome.AlreadyResolved, (await _h.Recovery.RecoverAsync(again, tx, attempt.Id)).Value);
        await again.CommitAsync();

        Assert.Equal(1, await EventCount(tx, "TRANSACTION_IN_DOUBT"));
    }

    private Task<int> EventCount(Domain.TransactionId tx, string eventType) =>
        _h.Query<int>(
            "SELECT count(*) FROM async.outbox_events WHERE aggregate_id = @id AND event_type = @eventType",
            new { id = tx.Value, eventType });

    [Fact]
    public async Task Recovery_leaves_young_attempts_alone()
    {
        var (tx, _) = await _h.ProcessingPayment();
        var attempt = await StartPayment(tx, _h.ProviderA);

        await using var session = await _h.Session();

        Assert.DoesNotContain((attempt.Id, tx), await _h.Recovery.FindCandidatesAsync(session, TimeSpan.FromHours(1), 1000));
    }

    [Fact]
    public async Task Recovery_after_the_transaction_was_already_resolved_does_not_regress_it()
    {
        var (tx, _) = await _h.ProcessingPayment();
        var attempt = await StartPayment(tx, _h.ProviderA);

        // A callback resolved the transaction before the sync outcome could be recorded.
        await using (var session = await _h.Session())
        {
            var transaction = Ok(await _h.Transactions.GetAsync(session, tx, Persistence.PostgreSql.Transactions.RowLock.ForUpdate))!;
            Ok(transaction.CompleteSuccess(Ctx("CALLBACK_SUCCESS", ChangeSource.Callback)));
            Ok(await _h.Ledger.PostPaymentAsync(session, new PostPaymentRequest(tx, _h.ProviderA.ProviderId)));
            Ok(await _h.Transactions.UpdateAsync(session, transaction));
            await session.CommitAsync();
        }

        await using (var session = await _h.Session())
        {
            Assert.Equal(AttemptRecoveryOutcome.AttemptClosed, (await _h.Recovery.RecoverAsync(session, tx, attempt.Id)).Value);
            await session.CommitAsync();
        }

        var after = await _h.Load(tx);
        Assert.Equal((ProcessingStatus.Success, FinancialStatus.Posted), (after.ProcessingStatus, after.FinancialStatus));
        Assert.True(Assert.Single(await _h.LoadAttempts(tx)).IsOutcomeRecorded);
    }

    private async Task<Domain.Attempts.TransactionAttempt> StartPayment(Domain.TransactionId tx, Domain.Routing.ProviderReference provider)
    {
        await using var session = await _h.Session();
        var transaction = Ok(await _h.Transactions.GetAsync(session, tx, Persistence.PostgreSql.Transactions.RowLock.ForUpdate))!;
        var attempt = Ok(await _h.Attempts.StartAsync(session, transaction, AttemptType.Payment, provider, "corr-1", "trace-1"));
        await session.CommitAsync();
        return attempt;
    }

    private async Task Record(Domain.Attempts.TransactionAttempt attempt, AttemptOutcome outcome)
    {
        await using var session = await _h.Session();
        Ok(await _h.Attempts.RecordOutcomeAsync(session, attempt, outcome));
        await session.CommitAsync();
    }
}
