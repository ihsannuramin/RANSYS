using Ransys.Domain.Common;
using Ransys.Domain.Transactions;
using static Ransys.Domain.Tests.TestData;
using static Ransys.Domain.Tests.Transactions.TransactionBuilder;

namespace Ransys.Domain.Tests.Transactions;

/// <summary>ADR-023: a refund child completes the original; the original never enters REFUND_PENDING.</summary>
public sealed class RefundCompletionTests
{
    [Fact]
    public void Refund_is_authorized_only_for_posted_or_partially_refunded_reserving_transactions()
    {
        Assert.True(Success(NewPayment()).AuthorizeRefund().IsSuccess);
        Assert.True(PartiallyRefunded(NewPayment()).AuthorizeRefund().IsSuccess);

        Assert.Equal(ErrorCodes.RefundNotAllowed, Refunded(NewPayment()).AuthorizeRefund().Error.Code);
        Assert.Equal(ErrorCodes.RefundNotAllowed, InDoubt(NewPayment()).AuthorizeRefund().Error.Code);
        Assert.Equal(ErrorCodes.RefundNotAllowed, Pending(NewPayment()).AuthorizeRefund().Error.Code);
        Assert.Equal(ErrorCodes.RefundNotAllowed, Failed(NewPayment()).AuthorizeRefund().Error.Code);
        Assert.Equal(ErrorCodes.RefundNotAllowed, ReversedCompensated(NewPayment()).AuthorizeRefund().Error.Code);
        Assert.Equal(ErrorCodes.RefundNotAllowed, Success(NewInquiry()).AuthorizeRefund().Error.Code);
    }

    [Fact]
    public void Partial_refund_moves_both_dimensions_to_partially_refunded_without_a_ledger_action()
    {
        var t = Success(NewPayment());
        t.ClearPendingStateChanges();

        var outcome = t.ApplyRefundCompleted(NewTransactionId(), fullyRefunded: false, Ctx("REFUND_COMPLETED")).Value;

        Assert.Equal(TransitionKind.Applied, outcome.Kind);
        Assert.Equal(LedgerAction.None, outcome.LedgerAction); // the caller posts the refund via PostRefundAsync
        Assert.Equal((ProcessingStatus.PartiallyRefunded, FinancialStatus.PartiallyRefunded), (t.ProcessingStatus, t.FinancialStatus));
        Assert.Equal(2, outcome.Changes.Count);
    }

    [Fact]
    public void Full_refund_from_success_goes_straight_to_refunded()
    {
        var t = Success(NewPayment());

        var outcome = t.ApplyRefundCompleted(NewTransactionId(), fullyRefunded: true, Ctx("REFUND_COMPLETED")).Value;

        Assert.Equal(LedgerAction.None, outcome.LedgerAction);
        Assert.Equal((ProcessingStatus.Refunded, FinancialStatus.Refunded), (t.ProcessingStatus, t.FinancialStatus));
    }

    [Fact]
    public void Another_partial_refund_is_no_change_and_the_final_one_refunds_fully()
    {
        var t = PartiallyRefunded(NewPayment());

        Assert.Equal(TransitionKind.NoChange, t.ApplyRefundCompleted(NewTransactionId(), false, Ctx("REFUND_COMPLETED")).Value.Kind);
        Assert.Equal(TransitionKind.Applied, t.ApplyRefundCompleted(NewTransactionId(), true, Ctx("REFUND_COMPLETED")).Value.Kind);
        Assert.Equal((ProcessingStatus.Refunded, FinancialStatus.Refunded), (t.ProcessingStatus, t.FinancialStatus));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Refunded_is_terminal_and_repeated_completion_is_no_change(bool fully)
    {
        var t = Refunded(NewPayment());

        Assert.Equal(TransitionKind.NoChange, t.ApplyRefundCompleted(NewTransactionId(), fully, Ctx("REFUND_COMPLETED")).Value.Kind);
    }

    [Fact]
    public void Refund_completion_is_invalid_for_unposted_failed_reversed_or_non_reserving_transactions()
    {
        Assert.Equal(ErrorCodes.InvalidStateTransition, InDoubt(NewPayment()).ApplyRefundCompleted(NewTransactionId(), true, Ctx()).Error.Code);
        Assert.Equal(ErrorCodes.InvalidStateTransition, Failed(NewPayment()).ApplyRefundCompleted(NewTransactionId(), true, Ctx()).Error.Code);
        Assert.Equal(ErrorCodes.InvalidStateTransition, ReversedCompensated(NewPayment()).ApplyRefundCompleted(NewTransactionId(), false, Ctx()).Error.Code);
        Assert.Equal(ErrorCodes.InvalidStateTransition, Success(NewInquiry()).ApplyRefundCompleted(NewTransactionId(), true, Ctx()).Error.Code);
    }

    [Fact]
    public void A_transaction_cannot_refund_itself()
    {
        var t = Success(NewPayment());

        Assert.Equal(ErrorCodes.OriginalTransactionSelfReference, t.ApplyRefundCompleted(t.Id, true, Ctx()).Error.Code);
    }

    [Fact]
    public void Late_original_results_after_refund_follow_the_existing_rules()
    {
        var t = PartiallyRefunded(NewPayment());

        Assert.Equal(TransitionKind.NoChange, t.CompleteSuccess(Ctx("PROVIDER_SUCCESS")).Value.Kind);
        Assert.Equal(TransitionKind.ConflictRecorded, t.CompleteFailure(Ctx("PROVIDER_DECLINED")).Value.Kind);
        Assert.Equal((ProcessingStatus.PartiallyRefunded, FinancialStatus.PartiallyRefunded), (t.ProcessingStatus, t.FinancialStatus));
    }
}

/// <summary>ADR-019: VOID fails closed; a confirmed VOID child never changes the original's processing or financial truth.</summary>
public sealed class VoidConfirmationTests
{
    [Fact]
    public void Void_is_authorized_in_the_same_states_as_a_reversal()
    {
        Func<Transaction>[] builders =
        [
            () => NewPayment(), () => Processing(NewPayment()), () => Pending(NewPayment()), () => InDoubt(NewPayment()),
            () => Success(NewPayment()), () => Failed(NewPayment()), () => ReversedCompensated(NewPayment()),
            () => PartiallyRefunded(NewPayment()), () => Success(NewInquiry()),
        ];

        foreach (var build in builders)
        {
            var reversal = build().AuthorizeReversal();
            var @void = build().AuthorizeVoid();

            Assert.Equal(reversal.IsSuccess, @void.IsSuccess);
            if (@void.IsFailure)
            {
                Assert.Equal(ErrorCodes.VoidNotAllowed, @void.Error.Code);
            }
        }
    }

    [Fact]
    public void Confirmed_void_records_a_reconciliation_exception_only()
    {
        var t = Success(NewPayment());
        t.ClearPendingStateChanges();

        var outcome = t.RecordVoidConfirmed(NewTransactionId(), Ctx("VOID_CONFIRMED")).Value;

        Assert.Equal(TransitionKind.ConflictRecorded, outcome.Kind);
        Assert.Equal(LedgerAction.None, outcome.LedgerAction);
        Assert.Equal((ProcessingStatus.Success, FinancialStatus.Posted), (t.ProcessingStatus, t.FinancialStatus));
        Assert.Equal(ReconciliationStatus.Exception, t.ReconciliationStatus);
        var change = Assert.Single(outcome.Changes);
        Assert.Equal(StatusDimension.Reconciliation, change.Dimension);
        Assert.Equal(ReasonCodes.VoidConfirmedRequiresReview, change.ReasonCode);
    }

    [Fact]
    public void Confirmed_void_is_idempotent_once_the_exception_exists()
    {
        var t = VoidConfirmed(NewPayment());
        t.ClearPendingStateChanges();

        var outcome = t.RecordVoidConfirmed(NewTransactionId(), Ctx("VOID_CONFIRMED")).Value;

        Assert.Equal(TransitionKind.ConflictRecorded, outcome.Kind);
        Assert.Empty(outcome.Changes);
        Assert.Empty(t.PendingStateChanges);
    }

    [Fact]
    public void Confirmed_void_on_an_in_doubt_original_keeps_the_reservation()
    {
        var t = InDoubt(NewPayment());

        t.RecordVoidConfirmed(NewTransactionId(), Ctx("VOID_CONFIRMED"));

        Assert.Equal((ProcessingStatus.InDoubt, FinancialStatus.Reserved), (t.ProcessingStatus, t.FinancialStatus));
    }

    [Fact]
    public void A_transaction_cannot_void_itself()
    {
        var t = Success(NewPayment());

        Assert.Equal(ErrorCodes.OriginalTransactionSelfReference, t.RecordVoidConfirmed(t.Id, Ctx()).Error.Code);
    }
}
