using Ransys.Domain.Transactions;
using P = Ransys.Domain.Transactions.ProcessingStatus;
using F = Ransys.Domain.Transactions.FinancialStatus;

namespace Ransys.Domain.Tests.Transactions;

/// <summary>
/// The transition tables are checked against an independent transcription of the State Transition Matrix
/// (§6 processing diagram, §23 financial diagram, §73 reference table).
/// </summary>
public sealed class TransitionTableTests
{
    private static readonly (P, P)[] DocumentedProcessingEdges =
    [
        (P.Received, P.Validated), (P.Received, P.Failed),
        (P.Validated, P.Processing), (P.Validated, P.Failed),
        (P.Processing, P.Success), (P.Processing, P.Failed), (P.Processing, P.Pending), (P.Processing, P.InDoubt),
        (P.Pending, P.Success), (P.Pending, P.Failed), (P.Pending, P.InDoubt), (P.Pending, P.ReversalPending),
        (P.InDoubt, P.Success), (P.InDoubt, P.Failed), (P.InDoubt, P.ReversalPending),
        (P.ReversalPending, P.Reversed), (P.ReversalPending, P.InDoubt),
        (P.Success, P.ReversalPending), (P.Success, P.RefundPending),
        (P.RefundPending, P.PartiallyRefunded), (P.RefundPending, P.Refunded), (P.RefundPending, P.Success),
        (P.PartiallyRefunded, P.RefundPending), (P.PartiallyRefunded, P.Refunded),
    ];

    private static readonly (F, F)[] DocumentedFinancialEdges =
    [
        (F.None, F.Reserved), (F.None, F.Adjusted),
        (F.Reserved, F.Posted), (F.Reserved, F.Released),
        (F.Posted, F.ReversalPending), (F.Posted, F.RefundPending),
        (F.ReversalPending, F.Reversed), (F.ReversalPending, F.Posted),
        (F.RefundPending, F.PartiallyRefunded), (F.RefundPending, F.Refunded), (F.RefundPending, F.Posted),
        (F.PartiallyRefunded, F.RefundPending), (F.PartiallyRefunded, F.Refunded),
    ];

    /// <summary>ADR-012: the original never enters REVERSAL_PENDING; it becomes REVERSED when the reversal child is confirmed.</summary>
    [Fact]
    public void Processing_table_is_the_documented_matrix_amended_by_adr_012()
    {
        var expected = DocumentedProcessingEdges
            .Where(e => e.Item1 != P.ReversalPending && e.Item2 != P.ReversalPending)
            .Concat([(P.Pending, P.Reversed), (P.InDoubt, P.Reversed), (P.Success, P.Reversed)])
            .ToHashSet();

        Assert.Equal(expected, TransactionTransitions.Processing.Edges.ToHashSet());
        Assert.DoesNotContain(TransactionTransitions.Processing.Edges, e => e.From == P.ReversalPending || e.To == P.ReversalPending);
    }

    [Fact]
    public void Financial_table_is_the_documented_matrix_amended_by_adr_012()
    {
        var expected = DocumentedFinancialEdges
            .Where(e => e.Item1 != F.ReversalPending && e.Item2 != F.ReversalPending)
            .Append((F.Posted, F.Reversed))
            .ToHashSet();

        Assert.Equal(expected, TransactionTransitions.Financial.Edges.ToHashSet());
    }

    [Theory]
    [InlineData(P.Failed, P.Success)]
    [InlineData(P.Success, P.Failed)]
    [InlineData(P.Reversed, P.Success)]
    [InlineData(P.Refunded, P.Success)]
    [InlineData(P.InDoubt, P.Pending)]
    [InlineData(P.Success, P.InDoubt)]
    [InlineData(P.Processing, P.Validated)]
    public void Forbidden_direct_edges_are_absent(P from, P to)
    {
        // §73 "No direct" rows and §65 (failover never toggles back to VALIDATED).
        Assert.False(TransactionTransitions.Processing.IsAllowed(from, to));
    }

    [Fact]
    public void Terminal_processing_states_have_no_outgoing_edges()
    {
        P[] terminal = [P.Failed, P.Reversed, P.Refunded];

        Assert.DoesNotContain(TransactionTransitions.Processing.Edges, e => terminal.Contains(e.From));
    }

    [Fact]
    public void Released_reserved_state_never_returns_to_active()
    {
        Assert.DoesNotContain(TransactionTransitions.Financial.Edges, e => e.From is F.Released or F.Reversed or F.Refunded);
    }
}
