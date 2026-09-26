using Ransys.Domain.Attempts;
using Ransys.Domain.Common;
using Ransys.Domain.Transactions;
using static Ransys.Domain.Tests.TestData;
using static Ransys.Domain.Tests.Transactions.TransactionBuilder;

namespace Ransys.Domain.Tests.Transactions;

/// <summary>Scenarios from State Transition Matrix §72 and the ledger/state rules they imply.</summary>
public sealed class TransactionAggregateTests
{
    [Fact]
    public void Create_starts_in_received_with_initial_history_row()
    {
        var id = NewTransactionId();
        var identity = TransactionIdentity.Create(id, "INV-001", null, TransactionFingerprint.Compute(FingerprintInput())).Value;
        var draft = new TransactionDraft(identity, TransactionType.Payment, Merchant, Channel, Product, Rp(100_000m),
            null, null, null, TransactionReferences.Create("INV-001").Value, ExtensionMetadata.Empty, T0);

        var t = Transaction.Create(draft).Value;

        Assert.Equal((ProcessingStatus.Received, FinancialStatus.None, ReconciliationStatus.Unmatched, SettlementStatus.NotApplicable),
            (t.ProcessingStatus, t.FinancialStatus, t.ReconciliationStatus, t.SettlementStatus));
        var history = Assert.Single(t.PendingStateChanges);
        Assert.Null(history.PreviousStatus);
        Assert.Equal("RECEIVED", history.NewStatus);
        Assert.Equal(id, t.Id);
    }

    [Fact]
    public void Create_rejects_missing_amount_missing_original_and_reference_mismatch()
    {
        var fingerprint = TransactionFingerprint.Compute(FingerprintInput());
        TransactionDraft Draft(TransactionType type, global::Ransys.Domain.Monetary.Money? amount, string bagReference = "INV-001") => new(
            TransactionIdentity.Create(NewTransactionId(), "INV-001", null, fingerprint).Value,
            type, Merchant, Channel, Product, amount, null, null, null,
            TransactionReferences.Create(bagReference).Value, ExtensionMetadata.Empty, T0);

        Assert.Equal(ErrorCodes.TransactionAmountRequired, Transaction.Create(Draft(TransactionType.Payment, null)).Error.Code);
        Assert.Equal(ErrorCodes.OriginalTransactionRequired, Transaction.Create(Draft(TransactionType.Refund, Rp(1m))).Error.Code);
        Assert.Equal(ErrorCodes.ReferenceMismatch, Transaction.Create(Draft(TransactionType.Payment, Rp(1m), "OTHER")).Error.Code);
        Assert.True(Transaction.Create(Draft(TransactionType.Inquiry, null)).IsSuccess);
    }

    [Fact]
    public void Case01_received_to_validated_captures_fees()
    {
        var t = Validated(NewPayment());

        Assert.Equal(ProcessingStatus.Validated, t.ProcessingStatus);
        Assert.Equal(Rp(2_500m), t.Fees!.MerchantChargeTotal);
        Assert.Equal(T0, t.ValidatedAt);
    }

    [Fact]
    public void Case02_invalid_request_fails_without_financial_effect()
    {
        var t = NewPayment();

        var outcome = t.CompleteFailure(Ctx("INVALID_REQUEST")).Value;

        Assert.Equal(LedgerAction.None, outcome.LedgerAction);
        Assert.Equal((ProcessingStatus.Failed, FinancialStatus.None), (t.ProcessingStatus, t.FinancialStatus));
    }

    [Fact]
    public void Case03_reserve_then_processing_requests_reserve_of_amount_plus_fee()
    {
        var t = Validated(NewPayment());

        var reserve = t.MarkReserved(Ctx("PAYMENT_PROCESSING")).Value;
        var processing = t.BeginProcessing(InitialRouting, Ctx("PROVIDER_PROCESSING")).Value;

        Assert.Equal(LedgerAction.Reserve, reserve.LedgerAction);
        Assert.Equal(Rp(102_500m), t.ReserveAmount);
        Assert.Equal(LedgerAction.None, processing.LedgerAction);
        Assert.Equal((ProcessingStatus.Processing, FinancialStatus.Reserved), (t.ProcessingStatus, t.FinancialStatus));
    }

    [Fact]
    public void Payment_cannot_start_processing_before_reserve_is_committed()
    {
        var t = Validated(NewPayment());

        Assert.Equal(ErrorCodes.InvalidStateTransition, t.BeginProcessing(InitialRouting, Ctx()).Error.Code);
    }

    [Fact]
    public void Inquiry_processes_without_reservation_and_cannot_reserve()
    {
        var t = Validated(NewInquiry());

        Assert.Equal(ErrorCodes.ReservationNotApplicable, t.MarkReserved(Ctx()).Error.Code);
        Assert.True(t.BeginProcessing(InitialRouting, Ctx()).IsSuccess);
        Assert.Equal(LedgerAction.None, t.CompleteSuccess(Ctx()).Value.LedgerAction);
        Assert.Equal(FinancialStatus.None, t.FinancialStatus);
    }

    [Fact]
    public void Pre_provider_failure_after_reserve_releases()
    {
        var t = Reserved(NewPayment());

        var outcome = t.CompleteFailure(Ctx("NO_ROUTE_AVAILABLE")).Value;

        Assert.Equal(LedgerAction.Release, outcome.LedgerAction);
        Assert.Equal((ProcessingStatus.Failed, FinancialStatus.Released), (t.ProcessingStatus, t.FinancialStatus));
    }

    [Fact]
    public void Case05_processing_to_success_posts()
    {
        var t = Processing(NewPayment());

        var outcome = t.CompleteSuccess(Ctx("PROVIDER_SUCCESS", ChangeSource.SyncProviderResponse)).Value;

        Assert.Equal(TransitionKind.Applied, outcome.Kind);
        Assert.Equal(LedgerAction.Post, outcome.LedgerAction);
        Assert.Equal((ProcessingStatus.Success, FinancialStatus.Posted), (t.ProcessingStatus, t.FinancialStatus));
        Assert.Equal(T0, t.FinancialPostedAt);
        Assert.Equal(T0, t.CompletedAt);
    }

    [Fact]
    public void Case06_processing_to_failed_releases()
    {
        var t = Processing(NewPayment());

        Assert.Equal(LedgerAction.Release, t.CompleteFailure(Ctx("PROVIDER_DECLINED")).Value.LedgerAction);
        Assert.Equal((ProcessingStatus.Failed, FinancialStatus.Released), (t.ProcessingStatus, t.FinancialStatus));
    }

    [Fact]
    public void Case07_processing_pending_success()
    {
        var t = Pending(NewPayment());

        Assert.Equal(FinancialStatus.Reserved, t.FinancialStatus);
        Assert.Equal(LedgerAction.Post, t.CompleteSuccess(Ctx("CALLBACK_SUCCESS", ChangeSource.Callback)).Value.LedgerAction);
    }

    [Fact]
    public void Case08_timeout_is_in_doubt_and_keeps_reservation()
    {
        var t = Processing(NewPayment());

        var outcome = t.MarkInDoubt(Ctx("PROVIDER_READ_TIMEOUT", ChangeSource.SyncProviderResponse)).Value;

        Assert.Equal(LedgerAction.None, outcome.LedgerAction);
        Assert.Equal((ProcessingStatus.InDoubt, FinancialStatus.Reserved), (t.ProcessingStatus, t.FinancialStatus));
        Assert.Null(t.CompletedAt);
        Assert.Single(outcome.Changes); // processing only; financial untouched
    }

    [Fact]
    public void Case09_in_doubt_resolved_success_by_status_check_posts_once()
    {
        var t = InDoubt(NewPayment());

        Assert.Equal(LedgerAction.Post, t.CompleteSuccess(Ctx("STATUS_CHECK_SUCCESS", ChangeSource.StatusCheck)).Value.LedgerAction);
        Assert.Equal(TransitionKind.NoChange, t.CompleteSuccess(Ctx("CALLBACK_SUCCESS", ChangeSource.Callback)).Value.Kind);
    }

    [Fact]
    public void Case10_in_doubt_resolved_failed_by_reconciliation_releases()
    {
        var t = InDoubt(NewPayment());

        Assert.Equal(LedgerAction.Release, t.CompleteFailure(Ctx("RECON_CONFIRMED_FAILURE", ChangeSource.Reconciliation)).Value.LedgerAction);
        Assert.Equal((ProcessingStatus.Failed, FinancialStatus.Released), (t.ProcessingStatus, t.FinancialStatus));
    }

    [Fact]
    public void Case11_in_doubt_reversal_releases_reservation()
    {
        var t = ReversalPendingUnposted(NewPayment());
        Assert.Equal(FinancialStatus.Reserved, t.FinancialStatus);

        var outcome = t.CompleteReversal(Ctx("REVERSAL_CONFIRMED")).Value;

        Assert.Equal(LedgerAction.ReversalRelease, outcome.LedgerAction);
        Assert.Equal((ProcessingStatus.Reversed, FinancialStatus.Released), (t.ProcessingStatus, t.FinancialStatus));
    }

    [Fact]
    public void Case12_reversal_after_success_is_compensating()
    {
        var t = Success(NewPayment());

        var begin = t.BeginReversal(Ctx("REVERSAL_REQUESTED")).Value;
        Assert.Equal(LedgerAction.None, begin.LedgerAction); // FS-04: no balance movement on request
        Assert.Equal(FinancialStatus.ReversalPending, t.FinancialStatus);

        var complete = t.CompleteReversal(Ctx("REVERSAL_CONFIRMED")).Value;
        Assert.Equal(LedgerAction.CompensatingReversal, complete.LedgerAction);
        Assert.Equal((ProcessingStatus.Reversed, FinancialStatus.Reversed), (t.ProcessingStatus, t.FinancialStatus));
    }

    [Fact]
    public void Case13_duplicate_success_callback_changes_nothing()
    {
        var t = Success(NewPayment());
        t.ClearPendingStateChanges();

        var outcome = t.CompleteSuccess(Ctx("CALLBACK_SUCCESS", ChangeSource.Callback)).Value;

        Assert.Equal(TransitionKind.NoChange, outcome.Kind);
        Assert.Equal(LedgerAction.None, outcome.LedgerAction);
        Assert.Empty(t.PendingStateChanges); // §60: no fake history row
    }

    [Fact]
    public void Case14_success_then_conflicting_failure_becomes_recon_exception()
    {
        var t = Success(NewPayment());

        var outcome = t.CompleteFailure(Ctx("PROVIDER_DECLINED", ChangeSource.Callback)).Value;

        Assert.Equal(TransitionKind.ConflictRecorded, outcome.Kind);
        Assert.Equal(LedgerAction.None, outcome.LedgerAction);
        Assert.Equal((ProcessingStatus.Success, FinancialStatus.Posted, ReconciliationStatus.Exception),
            (t.ProcessingStatus, t.FinancialStatus, t.ReconciliationStatus));
        var change = Assert.Single(outcome.Changes);
        Assert.Equal(StatusDimension.Reconciliation, change.Dimension);
        Assert.Equal(ReasonCodes.ConflictingProviderResult, change.ReasonCode);
    }

    [Fact]
    public void Case15_failed_then_contradictory_success_is_not_applied()
    {
        var t = Failed(NewPayment());

        var outcome = t.CompleteSuccess(Ctx("CALLBACK_SUCCESS", ChangeSource.Callback)).Value;

        Assert.Equal(TransitionKind.ConflictRecorded, outcome.Kind);
        Assert.Equal((ProcessingStatus.Failed, FinancialStatus.Released, ReconciliationStatus.Exception),
            (t.ProcessingStatus, t.FinancialStatus, t.ReconciliationStatus));

        // A second contradiction does not add another history row.
        Assert.Empty(t.CompleteSuccess(Ctx("CALLBACK_SUCCESS", ChangeSource.Callback)).Value.Changes);
    }

    [Fact]
    public void Late_timeout_after_success_does_not_regress()
    {
        var t = Success(NewPayment());

        Assert.Equal(ErrorCodes.InvalidStateTransition, t.MarkInDoubt(Ctx("PROVIDER_READ_TIMEOUT")).Error.Code);
        Assert.Equal(ProcessingStatus.Success, t.ProcessingStatus);
    }

    [Fact]
    public void Adr003_declined_reversal_of_posted_payment_returns_to_success()
    {
        var t = ReversalPendingPosted(NewPayment());

        var outcome = t.DeclineReversal(Ctx(ReasonCodes.ReversalDeclined)).Value;

        Assert.Equal(LedgerAction.None, outcome.LedgerAction);
        Assert.Equal((ProcessingStatus.Success, FinancialStatus.Posted), (t.ProcessingStatus, t.FinancialStatus));
    }

    [Fact]
    public void Adr003_declined_reversal_of_unposted_payment_returns_to_in_doubt_never_failed()
    {
        var t = ReversalPendingUnposted(NewPayment());

        t.DeclineReversal(Ctx(ReasonCodes.ReversalDeclined));

        Assert.Equal((ProcessingStatus.InDoubt, FinancialStatus.Reserved), (t.ProcessingStatus, t.FinancialStatus));
    }

    [Fact]
    public void Decline_reversal_requires_reversal_declined_reason()
    {
        var t = ReversalPendingPosted(NewPayment());

        Assert.Equal(ErrorCodes.ReasonCodeMismatch, t.DeclineReversal(Ctx("PROVIDER_DECLINED")).Error.Code);
        Assert.Equal(ProcessingStatus.ReversalPending, t.ProcessingStatus);
    }

    [Fact]
    public void Ambiguous_reversal_of_posted_payment_keeps_posting_and_success_needs_no_second_post()
    {
        var t = InDoubtAfterPostedReversal(NewPayment());
        Assert.Equal((ProcessingStatus.InDoubt, FinancialStatus.ReversalPending), (t.ProcessingStatus, t.FinancialStatus));

        var outcome = t.CompleteSuccess(Ctx("STATUS_CHECK_SUCCESS", ChangeSource.StatusCheck)).Value;

        Assert.Equal(LedgerAction.None, outcome.LedgerAction);
        Assert.Equal((ProcessingStatus.Success, FinancialStatus.Posted), (t.ProcessingStatus, t.FinancialStatus));
    }

    [Fact]
    public void Failover_is_allowed_only_when_previous_attempt_proves_not_sent()
    {
        var t = Processing(NewPayment());
        var attempt = TransactionAttempt.Start(new AttemptId(Guid.CreateVersion7()), t.Id, 1, AttemptType.Payment, ProviderRefA, "c", "t", T0).Value;

        // No outcome recorded yet: may have reached the provider.
        Assert.Equal(ErrorCodes.FailoverNotAllowed, t.RecordFailover(attempt, ProviderRefB, "PROVIDER_LINK_DOWN", T0).Error.Code);

        attempt.RecordOutcome(AttemptOutcome.Create(false, TransportStatus.ConnectionError).Value);
        Assert.True(t.RecordFailover(attempt, ProviderRefB, "PROVIDER_LINK_DOWN", T0).IsSuccess);

        Assert.Equal(ProcessingStatus.Processing, t.ProcessingStatus);
        Assert.Equal(ProviderRefA, t.Routing!.InitialProvider);
        Assert.Equal(ProviderRefB, t.Routing.CurrentProvider);
        Assert.Equal(1, t.Routing.FailoverCount);
    }

    [Fact]
    public void Failover_after_timeout_is_rejected()
    {
        var t = Processing(NewPayment());
        var attempt = TransactionAttempt.Start(new AttemptId(Guid.CreateVersion7()), t.Id, 1, AttemptType.Payment, ProviderRefA, "c", "t", T0).Value;
        attempt.RecordOutcome(AttemptOutcome.Create(true, TransportStatus.Timeout).Value);

        var result = t.RecordFailover(attempt, ProviderRefB, "TIMEOUT", T0);

        Assert.Equal(ErrorCodes.FailoverNotAllowed, result.Error.Code);
        Assert.Equal(ErrorCategory.Financial, result.Error.Category);
        Assert.Equal(ProviderRefA, t.Routing!.CurrentProvider);
    }

    [Fact]
    public void Failover_rejects_attempt_of_another_transaction_or_provider()
    {
        var t = Processing(NewPayment());
        var foreign = StartAttempt();
        foreign.RecordOutcome(AttemptOutcome.Create(false, TransportStatus.NotSent).Value);
        var otherProvider = TransactionAttempt.Start(new AttemptId(Guid.CreateVersion7()), t.Id, 1, AttemptType.Payment, ProviderRefB, "c", "t", T0).Value;
        otherProvider.RecordOutcome(AttemptOutcome.Create(false, TransportStatus.NotSent).Value);

        Assert.Equal(ErrorCodes.FailoverNotAllowed, t.RecordFailover(foreign, ProviderRefB, "DOWN", T0).Error.Code);
        Assert.Equal(ErrorCodes.FailoverNotAllowed, t.RecordFailover(otherProvider, ProviderRefB, "DOWN", T0).Error.Code);
    }

    [Fact]
    public void Duplicate_reserve_is_idempotent()
    {
        var t = Reserved(NewPayment());

        Assert.Equal(TransitionKind.NoChange, t.MarkReserved(Ctx()).Value.Kind);
    }

    [Fact]
    public void Zero_reserve_is_rejected()
    {
        var t = TransactionBuilder.New(TransactionType.Payment, Rp(0m));
        t.Validate(null, TransactionConfigurationSnapshot.None, Ctx());

        Assert.Equal(ErrorCodes.ReserveAmountNotPositive, t.MarkReserved(Ctx()).Error.Code);
        Assert.Equal(FinancialStatus.None, t.FinancialStatus);
    }

    [Fact]
    public void History_has_one_row_per_changed_dimension_with_shared_context()
    {
        var t = Processing(NewPayment());
        t.ClearPendingStateChanges();
        var attemptId = new AttemptId(Guid.CreateVersion7());
        var context = TransitionContext.Create("PROVIDER_SUCCESS", ChangeSource.SyncProviderResponse, T0, attemptId: attemptId, responseCode: "0000").Value;

        t.CompleteSuccess(context);

        Assert.Collection(
            t.PendingStateChanges,
            p => Assert.Equal((StatusDimension.Processing, "PROCESSING", "SUCCESS"), (p.Dimension, p.PreviousStatus, p.NewStatus)),
            f => Assert.Equal((StatusDimension.Financial, "RESERVED", "POSTED"), (f.Dimension, f.PreviousStatus, f.NewStatus)));
        Assert.All(t.PendingStateChanges, c => Assert.Equal(attemptId, c.AttemptId));
        Assert.Equal("0000", t.ResponseCode);
        Assert.Equal("PROVIDER_SUCCESS", t.ReasonCode);
    }

    [Fact]
    public void Transition_context_validates_reason_and_response_code()
    {
        Assert.Equal(ErrorCodes.Required, TransitionContext.Create(" ", ChangeSource.Core, T0).Error.Code);
        Assert.Equal(ErrorCodes.TooLong, TransitionContext.Create(new string('R', 65), ChangeSource.Core, T0).Error.Code);
        Assert.Equal(ErrorCodes.InvalidFormat, TransitionContext.Create("X", ChangeSource.Core, T0, responseCode: "00").Error.Code);
    }
}
