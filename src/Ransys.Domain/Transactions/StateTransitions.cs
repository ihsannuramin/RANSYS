using Ransys.Domain.Common;

namespace Ransys.Domain.Transactions;

/// <summary>The four orthogonal status dimensions (State Transition Matrix §2–3, ADR-004).</summary>
public enum StatusDimension
{
    Processing,
    Financial,
    Reconciliation,
    Settlement,
}

/// <summary>Origin of a transition (State Transition Matrix §59).</summary>
public enum ChangeSource
{
    Core,
    SyncProviderResponse,
    Callback,
    StatusCheck,
    Advice,
    Reconciliation,
    ManualAction,
    SystemRecovery,
}

/// <summary>What happened when a transition method was invoked.</summary>
public enum TransitionKind
{
    /// <summary>State changed; <see cref="TransitionOutcome.Changes"/> must be persisted with history rows.</summary>
    Applied,

    /// <summary>Same truth already recorded (duplicate/late consistent event). No state change, no posting.</summary>
    NoChange,

    /// <summary>
    /// A provider result contradicted established truth. Processing/financial state is untouched and
    /// reconciliation is set to EXCEPTION (State Transition Matrix §53–54, §56).
    /// </summary>
    ConflictRecorded,
}

/// <summary>
/// Ledger operation the application must execute in the same database transaction as the state change
/// (Canonical Data Model §164: the domain decides, the Ledger Posting Service performs).
/// Posting keys follow ADR-001.
/// </summary>
public enum LedgerAction
{
    None,

    /// <summary>OP-02, posting key <c>TX:&lt;id&gt;:RESERVE</c>.</summary>
    Reserve,

    /// <summary>OP-03/OP-06, posting key <c>TX:&lt;id&gt;:POST</c>.</summary>
    Post,

    /// <summary>OP-04/OP-07, posting key <c>TX:&lt;id&gt;:RELEASE</c>.</summary>
    Release,

    /// <summary>OP-08 reversal while the reservation is active, posting key <c>TX:&lt;id&gt;:REVERSAL_RELEASE</c>.</summary>
    ReversalRelease,

    /// <summary>OP-09 compensating reversal of a posted payment, posting key <c>TX:&lt;id&gt;:REVERSAL:&lt;ref&gt;</c>.</summary>
    CompensatingReversal,
}

/// <summary>
/// One real status change in one dimension; persisted as one <c>core.transaction_state_history</c> row (ADR-004).
/// Status values are canonical codes.
/// </summary>
public sealed record StateChange(
    StatusDimension Dimension,
    string? PreviousStatus,
    string NewStatus,
    string ReasonCode,
    string? ReasonDescription,
    ChangeSource Source,
    AttemptId? AttemptId,
    DateTimeOffset OccurredAt);

public sealed record TransitionOutcome(
    TransitionKind Kind,
    LedgerAction LedgerAction,
    IReadOnlyList<StateChange> Changes)
{
    public static TransitionOutcome NoChange { get; } = new(TransitionKind.NoChange, LedgerAction.None, []);
}

/// <summary>
/// Why/by whom/when a transition is requested. <see cref="ReasonCode"/> explains the transition
/// (State Transition Matrix §61); the reason catalog is versioned separately from the state enums.
/// </summary>
public sealed record TransitionContext
{
    /// <summary>DDL v1.1 <c>reason_code varchar(64)</c>.</summary>
    public const int MaxReasonCodeLength = 64;

    /// <summary>DDL v1.1 <c>reason_description varchar(500)</c>.</summary>
    public const int MaxReasonDescriptionLength = 500;

    private TransitionContext(
        string reasonCode,
        string? reasonDescription,
        ChangeSource source,
        AttemptId? attemptId,
        string? responseCode,
        DateTimeOffset occurredAt)
    {
        ReasonCode = reasonCode;
        ReasonDescription = reasonDescription;
        Source = source;
        AttemptId = attemptId;
        ResponseCode = responseCode;
        OccurredAt = occurredAt;
    }

    public string ReasonCode { get; }

    public string? ReasonDescription { get; }

    public ChangeSource Source { get; }

    /// <summary>Attempt whose result triggered the transition, if any.</summary>
    public AttemptId? AttemptId { get; }

    /// <summary>Optional 4-digit canonical response code to expose for this result.</summary>
    public string? ResponseCode { get; }

    public DateTimeOffset OccurredAt { get; }

    public static Result<TransitionContext> Create(
        string? reasonCode,
        ChangeSource source,
        DateTimeOffset occurredAt,
        string? reasonDescription = null,
        AttemptId? attemptId = null,
        string? responseCode = null)
    {
        if (!Enum.IsDefined(source))
        {
            return RansysError.Validation(ErrorCodes.OutOfRange, "Unknown change source.", "source");
        }

        var error = Text.FirstError(
            Text.Required(reasonCode, "reasonCode", MaxReasonCodeLength),
            Text.Optional(reasonDescription, "reasonDescription", MaxReasonDescriptionLength));
        if (error is not null)
        {
            return error;
        }

        if (responseCode is not null && (responseCode.Length != 4 || !responseCode.All(char.IsAsciiDigit)))
        {
            return RansysError.Validation(ErrorCodes.InvalidFormat, "Response code must be 4 digits.", "responseCode");
        }

        return new TransitionContext(reasonCode!, reasonDescription, source, attemptId, responseCode, occurredAt);
    }
}

/// <summary>
/// Allowed edges per dimension. Every transition method is checked against these tables, so a coding
/// mistake can never write an edge the State Transition Matrix does not allow.
/// </summary>
public static class TransactionTransitions
{
    /// <summary>State Transition Matrix §6 / §73, plus ADR-003 (REVERSAL_PENDING → SUCCESS).</summary>
    public static readonly TransitionTable<ProcessingStatus> Processing = new(
        (ProcessingStatus.Received, ProcessingStatus.Validated),
        (ProcessingStatus.Received, ProcessingStatus.Failed),
        (ProcessingStatus.Validated, ProcessingStatus.Processing),
        (ProcessingStatus.Validated, ProcessingStatus.Failed),
        (ProcessingStatus.Processing, ProcessingStatus.Success),
        (ProcessingStatus.Processing, ProcessingStatus.Failed),
        (ProcessingStatus.Processing, ProcessingStatus.Pending),
        (ProcessingStatus.Processing, ProcessingStatus.InDoubt),
        (ProcessingStatus.Pending, ProcessingStatus.Success),
        (ProcessingStatus.Pending, ProcessingStatus.Failed),
        (ProcessingStatus.Pending, ProcessingStatus.InDoubt),
        (ProcessingStatus.Pending, ProcessingStatus.ReversalPending),
        (ProcessingStatus.InDoubt, ProcessingStatus.Success),
        (ProcessingStatus.InDoubt, ProcessingStatus.Failed),
        (ProcessingStatus.InDoubt, ProcessingStatus.ReversalPending),
        (ProcessingStatus.ReversalPending, ProcessingStatus.Reversed),
        (ProcessingStatus.ReversalPending, ProcessingStatus.InDoubt),
        (ProcessingStatus.ReversalPending, ProcessingStatus.Success), // ADR-003, guarded
        (ProcessingStatus.Success, ProcessingStatus.ReversalPending),
        (ProcessingStatus.Success, ProcessingStatus.RefundPending),
        (ProcessingStatus.RefundPending, ProcessingStatus.PartiallyRefunded),
        (ProcessingStatus.RefundPending, ProcessingStatus.Refunded),
        (ProcessingStatus.RefundPending, ProcessingStatus.Success),
        (ProcessingStatus.PartiallyRefunded, ProcessingStatus.RefundPending),
        (ProcessingStatus.PartiallyRefunded, ProcessingStatus.Refunded));

    /// <summary>State Transition Matrix §23.</summary>
    public static readonly TransitionTable<FinancialStatus> Financial = new(
        (FinancialStatus.None, FinancialStatus.Reserved),
        (FinancialStatus.None, FinancialStatus.Adjusted),
        (FinancialStatus.Reserved, FinancialStatus.Posted),
        (FinancialStatus.Reserved, FinancialStatus.Released),
        (FinancialStatus.Posted, FinancialStatus.ReversalPending),
        (FinancialStatus.Posted, FinancialStatus.RefundPending),
        (FinancialStatus.ReversalPending, FinancialStatus.Reversed),
        (FinancialStatus.ReversalPending, FinancialStatus.Posted),
        (FinancialStatus.RefundPending, FinancialStatus.PartiallyRefunded),
        (FinancialStatus.RefundPending, FinancialStatus.Refunded),
        (FinancialStatus.RefundPending, FinancialStatus.Posted),
        (FinancialStatus.PartiallyRefunded, FinancialStatus.RefundPending),
        (FinancialStatus.PartiallyRefunded, FinancialStatus.Refunded));

    /// <summary>
    /// State Transition Matrix §32, plus conflict edges into EXCEPTION required by §54/§56
    /// (a contradicting provider result sets EXCEPTION regardless of the current reconciliation state).
    /// </summary>
    public static readonly TransitionTable<ReconciliationStatus> Reconciliation = new(
        (ReconciliationStatus.Unmatched, ReconciliationStatus.Pending),
        (ReconciliationStatus.Pending, ReconciliationStatus.Matched),
        (ReconciliationStatus.Pending, ReconciliationStatus.Exception),
        (ReconciliationStatus.Exception, ReconciliationStatus.Resolved),
        (ReconciliationStatus.Resolved, ReconciliationStatus.Matched),
        (ReconciliationStatus.Unmatched, ReconciliationStatus.Exception),
        (ReconciliationStatus.Matched, ReconciliationStatus.Exception),
        (ReconciliationStatus.Resolved, ReconciliationStatus.Exception));

    /// <summary>State Transition Matrix §41. No Phase 1 behavior uses it yet.</summary>
    public static readonly TransitionTable<SettlementStatus> Settlement = new(
        (SettlementStatus.NotApplicable, SettlementStatus.Pending),
        (SettlementStatus.Pending, SettlementStatus.Included),
        (SettlementStatus.Included, SettlementStatus.Approved),
        (SettlementStatus.Approved, SettlementStatus.ReadyToPay),
        (SettlementStatus.ReadyToPay, SettlementStatus.Settled),
        (SettlementStatus.Settled, SettlementStatus.Adjusted));
}

public sealed class TransitionTable<TStatus>
    where TStatus : struct, Enum
{
    private readonly HashSet<(TStatus From, TStatus To)> _edges;

    public TransitionTable(params (TStatus From, TStatus To)[] edges) => _edges = [.. edges];

    public IReadOnlyCollection<(TStatus From, TStatus To)> Edges => _edges;

    public bool IsAllowed(TStatus from, TStatus to) => _edges.Contains((from, to));
}
