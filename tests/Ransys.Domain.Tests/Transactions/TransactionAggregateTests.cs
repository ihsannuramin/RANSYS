using System.Collections.Immutable;
using System.Text.Json;
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
    public void Create_rejects_missing_original_and_reference_mismatch()
    {
        var fingerprint = TransactionFingerprint.Compute(FingerprintInput());
        TransactionDraft Draft(TransactionType type, global::Ransys.Domain.Monetary.Money amount, string bagReference = "INV-001") => new(
            TransactionIdentity.Create(NewTransactionId(), "INV-001", null, fingerprint).Value,
            type, Merchant, Channel, Product, amount, null, null, null,
            TransactionReferences.Create(bagReference).Value, ExtensionMetadata.Empty, T0);

        Assert.Equal(ErrorCodes.OriginalTransactionRequired, Transaction.Create(Draft(TransactionType.Refund, Rp(1m))).Error.Code);
        Assert.Equal(ErrorCodes.OriginalTransactionRequired, Transaction.Create(Draft(TransactionType.Void, Rp(1m))).Error.Code); // ADR-019
        Assert.Equal(ErrorCodes.ReferenceMismatch, Transaction.Create(Draft(TransactionType.Payment, Rp(1m), "OTHER")).Error.Code);
        Assert.True(Transaction.Create(Draft(TransactionType.Inquiry, Rp(0m))).IsSuccess);
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
    public void Case11_confirmed_reversal_of_in_doubt_payment_releases_reservation()
    {
        var t = InDoubt(NewPayment());
        var reversal = NewTransactionId();

        var outcome = t.ApplyReversalConfirmed(reversal, Ctx("REVERSAL_CONFIRMED")).Value;

        Assert.Equal(LedgerAction.ReversalRelease, outcome.LedgerAction);
        Assert.Equal((ProcessingStatus.Reversed, FinancialStatus.Released), (t.ProcessingStatus, t.FinancialStatus));
    }

    [Fact]
    public void Case12_confirmed_reversal_of_posted_payment_is_compensating()
    {
        var t = Success(NewPayment());

        var outcome = t.ApplyReversalConfirmed(NewTransactionId(), Ctx("REVERSAL_CONFIRMED")).Value;

        Assert.Equal(LedgerAction.CompensatingReversal, outcome.LedgerAction);
        Assert.Equal((ProcessingStatus.Reversed, FinancialStatus.Reversed), (t.ProcessingStatus, t.FinancialStatus));
    }

    [Fact]
    public void Adr012_starting_a_reversal_does_not_touch_the_original()
    {
        foreach (var t in new[] { Pending(NewPayment()), InDoubt(NewPayment()), Success(NewPayment()) })
        {
            var before = (t.ProcessingStatus, t.FinancialStatus, t.RowVersion);
            t.ClearPendingStateChanges();

            Assert.True(t.AuthorizeReversal().IsSuccess);
            Assert.Equal(before, (t.ProcessingStatus, t.FinancialStatus, t.RowVersion));
            Assert.Empty(t.PendingStateChanges);
        }
    }

    [Fact]
    public void Adr012_only_pending_in_doubt_or_posted_payments_are_reversible()
    {
        Assert.Equal(ErrorCodes.ReversalNotAllowed, Processing(NewPayment()).AuthorizeReversal().Error.Code);
        Assert.Equal(ErrorCodes.ReversalNotAllowed, Failed(NewPayment()).AuthorizeReversal().Error.Code);
        Assert.Equal(ErrorCodes.ReversalNotAllowed, ReversedCompensated(NewPayment()).AuthorizeReversal().Error.Code);
        Assert.Equal(ErrorCodes.ReversalNotAllowed, Success(NewInquiry()).AuthorizeReversal().Error.Code);
    }

    [Fact]
    public void Adr012_original_result_while_reversal_pending_is_applied_then_reversal_compensates()
    {
        // The case ADR-012 used to reject: IN_DOUBT original, reversal child in flight, original SUCCESS arrives.
        var t = InDoubt(NewPayment());
        Assert.True(t.AuthorizeReversal().IsSuccess);

        Assert.Equal(LedgerAction.Post, t.CompleteSuccess(Ctx("CALLBACK_SUCCESS", ChangeSource.Callback)).Value.LedgerAction);
        Assert.Equal(LedgerAction.CompensatingReversal, t.ApplyReversalConfirmed(NewTransactionId(), Ctx("REVERSAL_CONFIRMED")).Value.LedgerAction);
        Assert.Equal((ProcessingStatus.Reversed, FinancialStatus.Reversed), (t.ProcessingStatus, t.FinancialStatus));
    }

    [Fact]
    public void Adr012_reversal_confirmed_after_original_failed_moves_no_money()
    {
        var t = Failed(NewPayment());

        var outcome = t.ApplyReversalConfirmed(NewTransactionId(), Ctx("REVERSAL_CONFIRMED")).Value;

        Assert.Equal((TransitionKind.NoChange, LedgerAction.None), (outcome.Kind, outcome.LedgerAction));
        Assert.Equal((ProcessingStatus.Failed, FinancialStatus.Released), (t.ProcessingStatus, t.FinancialStatus));
    }

    [Fact]
    public void Adr012_reversal_child_has_its_own_non_reserving_lifecycle()
    {
        var child = Processing(NewReversalChild());

        Assert.Equal(FinancialStatus.None, child.FinancialStatus);
        Assert.Equal(LedgerAction.None, child.CompleteSuccess(Ctx("REVERSAL_CONFIRMED")).Value.LedgerAction);
        Assert.Equal(ProcessingStatus.Success, child.ProcessingStatus);
        Assert.NotNull(child.Identity.OriginalTransactionId);
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
    public void Adr012_declined_reversal_child_fails_alone()
    {
        var original = Success(NewPayment());
        var child = Processing(NewReversalChild());

        child.CompleteFailure(Ctx(ReasonCodes.ReversalDeclined, ChangeSource.SyncProviderResponse));

        Assert.Equal((ProcessingStatus.Failed, FinancialStatus.None), (child.ProcessingStatus, child.FinancialStatus));
        Assert.Equal((ProcessingStatus.Success, FinancialStatus.Posted), (original.ProcessingStatus, original.FinancialStatus));
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

    // ---------------------------------------------------------------- U1 (ADR-027 revised): MergeLatestProviderResult

    [Fact]
    public void MergeLatestProviderResult_records_the_first_evidence_with_no_prior_projection()
    {
        var t = NewPayment();
        var evidence = AttemptOutcome.Create(true, TransportStatus.Response, providerReference: "PRV-A", data: DataOf("field", "A")).Value;

        var outcome = t.MergeLatestProviderResult(evidence, ChangeSource.Callback, T0);

        Assert.Equal(ProviderResultMergeOutcome.Recorded, outcome);
        Assert.Equal("PRV-A", t.LatestProviderResult!.Evidence.ProviderReference);
        Assert.Equal("A", FieldOf(t.LatestProviderResult.Evidence.Data, "field"));
    }

    [Fact]
    public void MergeLatestProviderResult_enriches_same_identity_and_prefers_non_empty_data()
    {
        var t = NewPayment();
        var first = AttemptOutcome.Create(true, TransportStatus.Response, providerReference: "PRV-A", providerStan: "STAN-A").Value;
        var second = AttemptOutcome.Create(
            true, TransportStatus.Response, providerReference: "PRV-A", providerRrn: "RRN-A", data: DataOf("field", "full")).Value;

        Assert.Equal(ProviderResultMergeOutcome.Recorded, t.MergeLatestProviderResult(first, ChangeSource.SyncProviderResponse, T0));
        var outcome = t.MergeLatestProviderResult(second, ChangeSource.Callback, T0);

        Assert.Equal(ProviderResultMergeOutcome.Enriched, outcome);
        Assert.Equal(
            ("PRV-A", "STAN-A", "RRN-A"),
            (t.LatestProviderResult!.Evidence.ProviderReference, t.LatestProviderResult.Evidence.ProviderStan, t.LatestProviderResult.Evidence.ProviderRrn));
        Assert.Equal("full", FieldOf(t.LatestProviderResult.Evidence.Data, "field"));

        // A later report with the SAME identity but empty data never erases the richer data already known.
        var thin = AttemptOutcome.Create(true, TransportStatus.Response, providerReference: "PRV-A").Value;
        Assert.Equal(ProviderResultMergeOutcome.Enriched, t.MergeLatestProviderResult(thin, ChangeSource.Callback, T0));
        Assert.Equal("full", FieldOf(t.LatestProviderResult.Evidence.Data, "field"));
    }

    /// <summary>U1-3: a genuinely different provider reference is never silently swapped in.</summary>
    [Fact]
    public void MergeLatestProviderResult_returns_conflicting_identity_for_a_different_reference()
    {
        var t = NewPayment();
        var a = AttemptOutcome.Create(true, TransportStatus.Response, providerReference: "PRV-A").Value;
        var b = AttemptOutcome.Create(true, TransportStatus.Response, providerReference: "PRV-B").Value;

        var first = t.MergeLatestProviderResult(a, ChangeSource.Callback, T0);
        var second = t.MergeLatestProviderResult(b, ChangeSource.Callback, T0);

        Assert.Equal(ProviderResultMergeOutcome.Recorded, first);
        Assert.Equal(ProviderResultMergeOutcome.ConflictingIdentity, second);
        Assert.Equal("PRV-A", t.LatestProviderResult!.Evidence.ProviderReference);
    }

    [Fact]
    public void MergeLatestProviderResult_ignores_a_report_with_no_identity_at_all()
    {
        var t = NewPayment();
        var a = AttemptOutcome.Create(true, TransportStatus.Response, providerReference: "PRV-A").Value;
        var noIdentity = AttemptOutcome.Create(true, TransportStatus.Response).Value;

        Assert.Equal(ProviderResultMergeOutcome.Recorded, t.MergeLatestProviderResult(a, ChangeSource.Callback, T0));
        var outcome = t.MergeLatestProviderResult(noIdentity, ChangeSource.Callback, T0);

        Assert.Equal(ProviderResultMergeOutcome.IgnoredNoIdentity, outcome);
        Assert.Equal("PRV-A", t.LatestProviderResult!.Evidence.ProviderReference);
    }

    // ---------------------------------------------------------------- V1: per-field identity comparison

    /// <summary>
    /// V1-1: existing has all three identity fields set; incoming shares the SAME reference but a DIFFERENT STAN and
    /// RRN. The old collapsed-identity comparison (<c>ProviderReference ?? ProviderStan ?? ProviderRrn</c>) matched on
    /// the shared reference and then blindly overwrote STAN/RRN. Per-field comparison must catch the STAN/RRN
    /// mismatch and leave the whole projection untouched.
    /// </summary>
    [Fact]
    public void MergeLatestProviderResult_conflicting_stan_and_rrn_behind_a_shared_reference_is_a_conflict()
    {
        var t = NewPayment();
        var existing = AttemptOutcome.Create(true, TransportStatus.Response, providerReference: "P", providerStan: "S1", providerRrn: "R1").Value;
        var incoming = AttemptOutcome.Create(true, TransportStatus.Response, providerReference: "P", providerStan: "S2", providerRrn: "R2").Value;

        Assert.Equal(ProviderResultMergeOutcome.Recorded, t.MergeLatestProviderResult(existing, ChangeSource.Callback, T0));
        var outcome = t.MergeLatestProviderResult(incoming, ChangeSource.Callback, T0);

        Assert.Equal(ProviderResultMergeOutcome.ConflictingIdentity, outcome);
        Assert.Equal(
            ("P", "S1", "R1"),
            (t.LatestProviderResult!.Evidence.ProviderReference, t.LatestProviderResult.Evidence.ProviderStan, t.LatestProviderResult.Evidence.ProviderRrn));
    }

    /// <summary>
    /// V1-2: existing has no reference yet, only a shared RRN; incoming adds a reference under that same RRN. The old
    /// collapsed comparison treated this as a reference-vs-RRN mismatch (a false conflict); per-field comparison must
    /// recognize this as a valid enrichment (reference goes from null to P, RRN matches on its own field).
    /// </summary>
    [Fact]
    public void MergeLatestProviderResult_adding_a_reference_under_a_matching_rrn_enriches_not_conflicts()
    {
        var t = NewPayment();
        var existing = AttemptOutcome.Create(true, TransportStatus.Response, providerRrn: "R1").Value;
        var incoming = AttemptOutcome.Create(true, TransportStatus.Response, providerReference: "P", providerRrn: "R1").Value;

        Assert.Equal(ProviderResultMergeOutcome.Recorded, t.MergeLatestProviderResult(existing, ChangeSource.Callback, T0));
        var outcome = t.MergeLatestProviderResult(incoming, ChangeSource.Callback, T0);

        Assert.Equal(ProviderResultMergeOutcome.Enriched, outcome);
        Assert.Equal(
            ("P", "R1"), (t.LatestProviderResult!.Evidence.ProviderReference, t.LatestProviderResult.Evidence.ProviderRrn));
    }

    /// <summary>
    /// V1-3: existing has a reference and RRN; incoming repeats the same RRN but leaves the reference blank. A blank
    /// incoming field must never be treated as a conflict against a known existing value (that is what "existing
    /// non-null, incoming null → keep existing" means) — it also must not be misread as a reference-vs-RRN mismatch.
    /// </summary>
    [Fact]
    public void MergeLatestProviderResult_partial_duplicate_with_blank_reference_field_is_not_a_conflict()
    {
        var t = NewPayment();
        var existing = AttemptOutcome.Create(true, TransportStatus.Response, providerReference: "P", providerRrn: "R1").Value;
        var incoming = AttemptOutcome.Create(true, TransportStatus.Response, providerRrn: "R1").Value;

        Assert.Equal(ProviderResultMergeOutcome.Recorded, t.MergeLatestProviderResult(existing, ChangeSource.Callback, T0));
        var outcome = t.MergeLatestProviderResult(incoming, ChangeSource.Callback, T0);

        Assert.NotEqual(ProviderResultMergeOutcome.ConflictingIdentity, outcome);
        Assert.Equal("P", t.LatestProviderResult!.Evidence.ProviderReference);
    }

    /// <summary>
    /// V1-4: Data merge is a whole-payload snapshot, never a key-by-key splice. A later non-empty report with FEWER
    /// keys than the existing payload must fully replace it, not merge business keys from two different reports.
    /// </summary>
    [Fact]
    public void MergeLatestProviderResult_data_merge_is_a_snapshot_not_a_key_by_key_splice()
    {
        var t = NewPayment();
        var existingData = ImmutableDictionary<string, JsonElement>.Empty
            .Add("a", JsonSerializer.SerializeToElement("1"))
            .Add("b", JsonSerializer.SerializeToElement("2"));
        var incomingData = ImmutableDictionary<string, JsonElement>.Empty.Add("a", JsonSerializer.SerializeToElement("3"));
        var existing = AttemptOutcome.Create(true, TransportStatus.Response, providerReference: "P", data: existingData).Value;
        var incoming = AttemptOutcome.Create(true, TransportStatus.Response, providerReference: "P", data: incomingData).Value;

        Assert.Equal(ProviderResultMergeOutcome.Recorded, t.MergeLatestProviderResult(existing, ChangeSource.Callback, T0));
        var outcome = t.MergeLatestProviderResult(incoming, ChangeSource.Callback, T0);

        Assert.Equal(ProviderResultMergeOutcome.Enriched, outcome);
        var data = t.LatestProviderResult!.Evidence.Data;
        Assert.Equal("3", FieldOf(data, "a"));
        Assert.Null(FieldOf(data, "b"));
        Assert.Single(data!);
    }

    private static IReadOnlyDictionary<string, JsonElement> DataOf(string field, string value) =>
        ImmutableDictionary<string, JsonElement>.Empty.Add(field, JsonSerializer.SerializeToElement(value));

    private static string? FieldOf(IReadOnlyDictionary<string, JsonElement>? data, string field) =>
        data is not null && data.TryGetValue(field, out var value) ? value.GetString() : null;
}
