using Ransys.Domain.Common;
using Ransys.Domain.Transactions;
using static Ransys.Domain.Tests.Transactions.TransactionBuilder;

namespace Ransys.Domain.Tests.Transactions;

/// <summary>
/// Runs every transition method against every reachable state. Guarantees:
/// <list type="bullet">
/// <item>no method throws (invalid requests are controlled results);</item>
/// <item>a failed or no-change call leaves every status untouched;</item>
/// <item>every applied change is an edge of the State Transition Matrix;</item>
/// <item>ledger actions only accompany the financial changes that justify them.</item>
/// </list>
/// </summary>
public sealed class ExhaustiveTransitionTests
{
    private static readonly (string Name, Func<Transaction, Result<TransitionOutcome>> Invoke)[] Methods =
    [
        ("Validate", t => t.Validate(null, TransactionConfigurationSnapshot.None, Ctx())),
        ("MarkReserved", t => t.MarkReserved(Ctx())),
        ("BeginProcessing", t => t.BeginProcessing(InitialRouting, Ctx())),
        ("MarkPending", t => t.MarkPending(Ctx())),
        ("MarkInDoubt", t => t.MarkInDoubt(Ctx())),
        ("CompleteSuccess", t => t.CompleteSuccess(Ctx())),
        ("CompleteFailure", t => t.CompleteFailure(Ctx())),
        ("ApplyReversalConfirmed", t => t.ApplyReversalConfirmed(new TransactionId(Guid.CreateVersion7()), Ctx("REVERSAL_CONFIRMED"))),
        ("ApplyRefundCompleted(partial)", t => t.ApplyRefundCompleted(new TransactionId(Guid.CreateVersion7()), false, Ctx("REFUND_COMPLETED"))),
        ("ApplyRefundCompleted(full)", t => t.ApplyRefundCompleted(new TransactionId(Guid.CreateVersion7()), true, Ctx("REFUND_COMPLETED"))),
        ("RecordVoidConfirmed", t => t.RecordVoidConfirmed(new TransactionId(Guid.CreateVersion7()), Ctx("VOID_CONFIRMED"))),
    ];

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var (state, _) in ReachableStates())
        {
            foreach (var (method, _) in Methods)
            {
                data.Add(state, method);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_method_in_every_state_respects_the_matrix(string state, string method)
    {
        var t = ReachableStates().Single(s => s.Name == state).Build();
        t.ClearPendingStateChanges();
        var before = Snapshot(t);

        var result = Methods.Single(m => m.Name == method).Invoke(t);

        if (result.IsFailure || result.Value.Kind == TransitionKind.NoChange)
        {
            Assert.Equal(before, Snapshot(t));
            Assert.Empty(t.PendingStateChanges);
            return;
        }

        var outcome = result.Value;
        Assert.Equal(outcome.Changes, t.PendingStateChanges);
        foreach (var change in outcome.Changes)
        {
            Assert.True(IsMatrixEdge(change), $"{state} {method}: {change.Dimension} {change.PreviousStatus}->{change.NewStatus}");
        }

        var financialChange = outcome.Changes.SingleOrDefault(c => c.Dimension == StatusDimension.Financial);
        var expectedLedger = (financialChange?.PreviousStatus, financialChange?.NewStatus) switch
        {
            ("NONE", "RESERVED") => LedgerAction.Reserve,
            ("RESERVED", "POSTED") => LedgerAction.Post,
            ("RESERVED", "RELEASED") => t.ProcessingStatus == ProcessingStatus.Reversed ? LedgerAction.ReversalRelease : LedgerAction.Release,
            ("POSTED", "REVERSED") => LedgerAction.CompensatingReversal,
            _ => LedgerAction.None,
        };
        Assert.Equal(expectedLedger, outcome.LedgerAction);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Terminal_states_never_change_processing_or_financial_truth(string state, string method)
    {
        if (!state.Contains(":FAILED", StringComparison.Ordinal) && !state.Contains(":REVERSED", StringComparison.Ordinal)
            && !state.Contains(":REFUNDED", StringComparison.Ordinal))
        {
            return;
        }

        var t = ReachableStates().Single(s => s.Name == state).Build();
        var before = (t.ProcessingStatus, t.FinancialStatus);

        Methods.Single(m => m.Name == method).Invoke(t);

        Assert.Equal(before, (t.ProcessingStatus, t.FinancialStatus));
    }

    private static bool IsMatrixEdge(StateChange change) => change.Dimension switch
    {
        StatusDimension.Processing => TransactionTransitions.Processing.IsAllowed(
            CanonicalCodes.ProcessingStatus.Parse(change.PreviousStatus!), CanonicalCodes.ProcessingStatus.Parse(change.NewStatus)),
        StatusDimension.Financial => TransactionTransitions.Financial.IsAllowed(
            CanonicalCodes.FinancialStatus.Parse(change.PreviousStatus!), CanonicalCodes.FinancialStatus.Parse(change.NewStatus)),
        StatusDimension.Reconciliation => TransactionTransitions.Reconciliation.IsAllowed(
            CanonicalCodes.ReconciliationStatus.Parse(change.PreviousStatus!), CanonicalCodes.ReconciliationStatus.Parse(change.NewStatus)),
        _ => false,
    };

    private static object Snapshot(Transaction t) => (
        t.ProcessingStatus, t.FinancialStatus, t.ReconciliationStatus, t.SettlementStatus,
        t.ReserveAmount, t.ReasonCode, t.CompletedAt, t.FinancialPostedAt, t.Routing);
}
