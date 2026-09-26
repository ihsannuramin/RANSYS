using Ransys.Domain.Attempts;
using Ransys.Domain.Common;
using Ransys.Domain.Fees;
using Ransys.Domain.Monetary;
using Ransys.Domain.Routing;

namespace Ransys.Domain.Transactions;

/// <summary>Input for <see cref="Transaction.Create"/>.</summary>
public sealed record TransactionDraft(
    TransactionIdentity Identity,
    TransactionType Type,
    MerchantId MerchantId,
    ChannelId ChannelId,
    ProductId ProductId,
    Money Amount,
    Customer? Customer,
    TransactionEndpoint? Source,
    TransactionEndpoint? Destination,
    TransactionReferences References,
    ExtensionMetadata Metadata,
    DateTimeOffset ReceivedAt);

/// <summary>
/// Behavior-rich transaction aggregate (Canonical Data Model §5, §103–104, §162–164; main.md §11).
/// <para>
/// Status changes only through transition methods that enforce the State Transition Matrix per dimension.
/// Each method returns a <see cref="TransitionOutcome"/> telling the application which ledger action to execute
/// in the same database transaction; the aggregate never touches the database, wallet or ledger itself.
/// </para>
/// <para>
/// Invalid transitions return <see cref="ErrorCodes.InvalidStateTransition"/> and change nothing
/// (State Transition Matrix §52). Duplicate consistent results return <see cref="TransitionKind.NoChange"/>;
/// contradicting provider results return <see cref="TransitionKind.ConflictRecorded"/> (§53–56).
/// </para>
/// </summary>
public sealed class Transaction
{
    private readonly List<StateChange> _pendingStateChanges = [];

    private Transaction(TransactionDraft draft)
    {
        Identity = draft.Identity;
        Type = draft.Type;
        MerchantId = draft.MerchantId;
        ChannelId = draft.ChannelId;
        ProductId = draft.ProductId;
        Amount = draft.Amount;
        Customer = draft.Customer;
        Source = draft.Source;
        Destination = draft.Destination;
        References = draft.References;
        Metadata = draft.Metadata;
        ReceivedAt = draft.ReceivedAt;
        UpdatedAt = draft.ReceivedAt;
        ProcessingStatus = ProcessingStatus.Received;
        FinancialStatus = FinancialStatus.None;
        ReconciliationStatus = ReconciliationStatus.Unmatched;
        SettlementStatus = SettlementStatus.NotApplicable;
        ReasonCode = ReasonCodes.TransactionReceived;
    }

    public TransactionId Id => Identity.RansysTransactionId;

    public TransactionIdentity Identity { get; }

    public TransactionType Type { get; }

    public MerchantId MerchantId { get; }

    public ChannelId ChannelId { get; }

    public ProductId ProductId { get; }

    /// <summary>
    /// Principal. Always present (Canonical Contracts <c>CanonicalTransaction.Amount</c>, DDL <c>amount NOT NULL</c>);
    /// non-monetary requests such as inquiries carry an explicit zero supplied by the caller.
    /// </summary>
    public Money Amount { get; }

    /// <summary>Captured at validation; financial terms never change afterwards.</summary>
    public FeeComponents? Fees { get; private set; }

    /// <summary>Principal + guaranteed merchant charges, fixed at reservation.</summary>
    public Money? ReserveAmount { get; private set; }

    public Customer? Customer { get; }

    public TransactionEndpoint? Source { get; }

    public TransactionEndpoint? Destination { get; }

    public TransactionReferences References { get; }

    public RoutingDecision? Routing { get; private set; }

    public TransactionConfigurationSnapshot Configuration { get; private set; } = TransactionConfigurationSnapshot.None;

    public ExtensionMetadata Metadata { get; }

    public ProcessingStatus ProcessingStatus { get; private set; }

    public FinancialStatus FinancialStatus { get; private set; }

    public ReconciliationStatus ReconciliationStatus { get; private set; }

    public SettlementStatus SettlementStatus { get; private set; }

    public string? ResponseCode { get; private set; }

    public string? ReasonCode { get; private set; }

    public string? ReasonDescription { get; private set; }

    public DateTimeOffset ReceivedAt { get; }

    public DateTimeOffset? ValidatedAt { get; private set; }

    public DateTimeOffset? FinancialPostedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool RequiresReservation => TransactionTypeRules.RequiresReservation(Type);

    /// <summary>
    /// Optimistic concurrency version of the persisted row (<c>core.transactions.row_version</c>);
    /// 0 for a transaction that has never been persisted (State Transition Matrix §57).
    /// </summary>
    public long RowVersion { get; private set; }

    public bool IsPersisted => RowVersion > 0;

    /// <summary>State changes not yet persisted, in order.</summary>
    public IReadOnlyList<StateChange> PendingStateChanges => _pendingStateChanges;

    public void ClearPendingStateChanges() => _pendingStateChanges.Clear();

    /// <summary>
    /// Called by the persistence layer after a successful insert/update commit: records the new row version
    /// and clears the pending history that was written.
    /// </summary>
    public void MarkPersisted(long rowVersion)
    {
        if (rowVersion <= RowVersion)
        {
            throw new InvalidOperationException($"Row version must increase (current {RowVersion}, new {rowVersion}).");
        }

        RowVersion = rowVersion;
        _pendingStateChanges.Clear();
    }

    /// <summary>
    /// Rebuilds a persisted transaction. Cross-dimension invariants are re-checked; persisted state that
    /// violates them is reported (fail closed) instead of being loaded.
    /// </summary>
    public static Result<Transaction> Rehydrate(TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.RowVersion <= 0)
        {
            return PersistedStateInvalid(snapshot, "row version must be positive");
        }

        if (snapshot.Fees is not null && snapshot.Fees.Currency != snapshot.Amount.Currency)
        {
            return PersistedStateInvalid(snapshot, "fee currency differs from transaction currency");
        }

        if (snapshot.ReserveAmount is not null && snapshot.ReserveAmount.Currency != snapshot.Amount.Currency)
        {
            return PersistedStateInvalid(snapshot, "reserve currency differs from transaction currency");
        }

        var transaction = new Transaction(snapshot.ToDraft())
        {
            Fees = snapshot.Fees,
            ReserveAmount = snapshot.ReserveAmount,
            Routing = snapshot.Routing,
            Configuration = snapshot.Configuration,
            ProcessingStatus = snapshot.ProcessingStatus,
            FinancialStatus = snapshot.FinancialStatus,
            ReconciliationStatus = snapshot.ReconciliationStatus,
            SettlementStatus = snapshot.SettlementStatus,
            ResponseCode = snapshot.ResponseCode,
            ReasonCode = snapshot.ReasonCode,
            ReasonDescription = snapshot.ReasonDescription,
            ValidatedAt = snapshot.ValidatedAt,
            FinancialPostedAt = snapshot.FinancialPostedAt,
            CompletedAt = snapshot.CompletedAt,
            UpdatedAt = snapshot.UpdatedAt,
            RowVersion = snapshot.RowVersion,
        };

        var validatedStateHasFees = transaction.ValidatedAt is null || transaction.Fees is not null;
        if (!transaction.InvariantsHold() || !validatedStateHasFees)
        {
            return PersistedStateInvalid(snapshot, "cross-dimension invariants do not hold");
        }

        return transaction;
    }

    private static RansysError PersistedStateInvalid(TransactionSnapshot snapshot, string detail) =>
        new(ErrorCodes.PersistedStateInvalid, ErrorCategory.Internal,
            $"Persisted transaction {snapshot.Identity.RansysTransactionId} is inconsistent: {detail}.");

    /// <summary>PS-01: creates the transaction in RECEIVED / NONE / UNMATCHED / NOT_APPLICABLE.</summary>
    public static Result<Transaction> Create(TransactionDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(draft.Identity);
        ArgumentNullException.ThrowIfNull(draft.References);
        ArgumentNullException.ThrowIfNull(draft.Metadata);
        ArgumentNullException.ThrowIfNull(draft.Amount);

        if (!Enum.IsDefined(draft.Type))
        {
            return RansysError.Validation(ErrorCodes.OutOfRange, "Unknown transaction type.", "transactionType");
        }

        if (!string.Equals(draft.References.ClientReference, draft.Identity.ClientReference, StringComparison.Ordinal))
        {
            return RansysError.Validation(
                ErrorCodes.ReferenceMismatch, "Reference bag client reference must equal the identity client reference.", "references");
        }

        if (TransactionTypeRules.RequiresOriginalTransaction(draft.Type) && draft.Identity.OriginalTransactionId is null)
        {
            return RansysError.Validation(
                ErrorCodes.OriginalTransactionRequired, "Refund and reversal transactions must reference the original transaction.", "originalTransactionId");
        }

        var transaction = new Transaction(draft);
        transaction._pendingStateChanges.Add(new StateChange(
            StatusDimension.Processing,
            PreviousStatus: null,
            NewStatus: CanonicalCodes.ProcessingStatus.ToCode(ProcessingStatus.Received),
            ReasonCodes.TransactionReceived,
            ReasonDescription: null,
            ChangeSource.Core,
            AttemptId: null,
            draft.ReceivedAt));
        transaction.AssertInvariants();
        return transaction;
    }

    /// <summary>
    /// PS-02: RECEIVED → VALIDATED. Captures fee components and configuration versions; these financial terms
    /// are authoritative for the rest of the transaction's life.
    /// </summary>
    public Result<TransitionOutcome> Validate(
        FeeComponents? fees, TransactionConfigurationSnapshot configuration, TransitionContext context)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(context);

        if (ProcessingStatus != ProcessingStatus.Received)
        {
            return Invalid(nameof(Validate));
        }

        if (fees is not null && fees.Currency != Amount.Currency)
        {
            return RansysError.Validation(ErrorCodes.FeeCurrencyMismatch, "Fees must use the transaction currency.", "fees");
        }

        var changes = new List<StateChange>();
        ChangeProcessing(ProcessingStatus.Validated, context, changes);
        Fees = fees ?? FeeComponents.None(Amount.Currency);
        Configuration = configuration;
        ValidatedAt = context.OccurredAt;
        return Commit(context, changes, LedgerAction.None);
    }

    /// <summary>
    /// FS-01: NONE → RESERVED for reserving families, while VALIDATED. The application performs OP-02
    /// (lock wallet, check available, reserve) in the same database transaction.
    /// </summary>
    public Result<TransitionOutcome> MarkReserved(TransitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!RequiresReservation)
        {
            return RansysError.Validation(
                ErrorCodes.ReservationNotApplicable, $"{CanonicalCodes.TransactionType.ToCode(Type)} does not reserve balance.", "transactionType");
        }

        if (ProcessingStatus == ProcessingStatus.Validated && FinancialStatus == FinancialStatus.Reserved)
        {
            return TransitionOutcome.NoChange;
        }

        if (ProcessingStatus != ProcessingStatus.Validated || FinancialStatus != FinancialStatus.None)
        {
            return Invalid(nameof(MarkReserved));
        }

        var reserve = Fees!.CalculateReserveAmount(Amount);
        if (reserve.IsFailure)
        {
            return reserve.Error;
        }

        // Ledger entries must be > 0 (DDL ck_ledger_entry_amount), so a zero reserve cannot be posted.
        if (reserve.Value.IsZero)
        {
            return RansysError.Validation(ErrorCodes.ReserveAmountNotPositive, "Reserve amount must be greater than zero.", "amount");
        }

        var changes = new List<StateChange>();
        ChangeFinancial(FinancialStatus.Reserved, context, changes);
        ReserveAmount = reserve.Value;
        return Commit(context, changes, LedgerAction.Reserve);
    }

    /// <summary>
    /// PS-04: VALIDATED → PROCESSING. A reserving transaction must already be RESERVED (reserve committed before
    /// any provider call, Architecture Spec §12).
    /// </summary>
    public Result<TransitionOutcome> BeginProcessing(RoutingDecision routing, TransitionContext context)
    {
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(context);

        if (ProcessingStatus == ProcessingStatus.Processing && Routing == routing)
        {
            return TransitionOutcome.NoChange;
        }

        var reservationReady = RequiresReservation
            ? FinancialStatus == FinancialStatus.Reserved
            : FinancialStatus == FinancialStatus.None;
        if (ProcessingStatus != ProcessingStatus.Validated || !reservationReady)
        {
            return Invalid(nameof(BeginProcessing));
        }

        if (routing.FailoverCount != 0)
        {
            return RansysError.Validation(
                ErrorCodes.RoutingInvalidDecision, "Processing must start from an initial routing decision.", "routing");
        }

        var changes = new List<StateChange>();
        ChangeProcessing(ProcessingStatus.Processing, context, changes);
        Routing = routing;
        return Commit(context, changes, LedgerAction.None);
    }

    /// <summary>
    /// Pre-send failover (State Transition Matrix §15, §65; ADR-005). Allowed only while PROCESSING and only when
    /// the previous attempt on the current provider <em>proves</em> the request was never sent.
    /// Processing stays PROCESSING; a new attempt follows on the next provider.
    /// </summary>
    public Result RecordFailover(
        TransactionAttempt previousAttempt, ProviderReference nextProvider, string reason, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(previousAttempt);
        ArgumentNullException.ThrowIfNull(nextProvider);

        if (ProcessingStatus != ProcessingStatus.Processing || Routing is null)
        {
            return Invalid(nameof(RecordFailover));
        }

        if (previousAttempt.TransactionId != Id || previousAttempt.Provider.ProviderId != Routing.CurrentProvider.ProviderId)
        {
            return RansysError.Validation(
                ErrorCodes.FailoverNotAllowed, "The attempt does not belong to this transaction's current provider.", "attempt");
        }

        if (!previousAttempt.ProvesRequestNotSent)
        {
            return RansysError.Financial(
                ErrorCodes.FailoverNotAllowed,
                "Failover is not allowed: the previous request may have reached the provider (IN_DOUBT handling applies).");
        }

        var next = Routing.FailoverTo(nextProvider, reason, occurredAt);
        if (next.IsFailure)
        {
            return next.Error;
        }

        Routing = next.Value;
        UpdatedAt = occurredAt;
        return Result.Success();
    }

    /// <summary>PS-07: PROCESSING → PENDING (provider explicitly reports pending). Reservation stays.</summary>
    public Result<TransitionOutcome> MarkPending(TransitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return ProcessingStatus switch
        {
            ProcessingStatus.Pending => TransitionOutcome.NoChange,
            ProcessingStatus.Processing => Apply(context, ProcessingStatus.Pending, financial: null, LedgerAction.None),
            _ => Invalid(nameof(MarkPending)),
        };
    }

    /// <summary>
    /// PS-08: PROCESSING / PENDING / REVERSAL_PENDING → IN_DOUBT when the final provider result cannot be proven.
    /// Financial status is preserved: the reservation is never released automatically (OP-05).
    /// </summary>
    public Result<TransitionOutcome> MarkInDoubt(TransitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return ProcessingStatus switch
        {
            ProcessingStatus.InDoubt => TransitionOutcome.NoChange,
            ProcessingStatus.Processing or ProcessingStatus.Pending or ProcessingStatus.ReversalPending =>
                Apply(context, ProcessingStatus.InDoubt, financial: null, LedgerAction.None),
            _ => Invalid(nameof(MarkInDoubt)),
        };
    }

    /// <summary>
    /// PS-05: definitive provider SUCCESS of the original request, from PROCESSING / PENDING / IN_DOUBT.
    /// Reserving transactions move RESERVED → POSTED and require OP-03 (POST).
    /// </summary>
    public Result<TransitionOutcome> CompleteSuccess(TransitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        switch (ProcessingStatus)
        {
            case ProcessingStatus.Processing or ProcessingStatus.Pending or ProcessingStatus.InDoubt:
                if (!RequiresReservation)
                {
                    return Apply(context, ProcessingStatus.Success, financial: null, LedgerAction.None);
                }

                return FinancialStatus switch
                {
                    FinancialStatus.Reserved => Apply(context, ProcessingStatus.Success, FinancialStatus.Posted, LedgerAction.Post),

                    // IN_DOUBT reached from an ambiguous reversal of an already-posted payment: the original stays posted.
                    FinancialStatus.ReversalPending => Apply(context, ProcessingStatus.Success, FinancialStatus.Posted, LedgerAction.None),
                    _ => Invalid(nameof(CompleteSuccess)),
                };

            // Original success is already established (or later reversed/refunded consistently): duplicate/late report.
            case ProcessingStatus.Success or ProcessingStatus.RefundPending or ProcessingStatus.PartiallyRefunded
                or ProcessingStatus.Refunded or ProcessingStatus.Reversed:
                return TransitionOutcome.NoChange;

            case ProcessingStatus.ReversalPending when FinancialStatus == FinancialStatus.ReversalPending:
                return TransitionOutcome.NoChange;

            // §53: FAILED was assigned only on definitive proof; a later SUCCESS is a contradiction, never FAILED → SUCCESS.
            case ProcessingStatus.Failed:
                return RecordConflict(context);

            // TODO / Architecture Decision Required (ADR-012): original result arriving while a reversal of a
            // not-yet-posted transaction is in flight. Rejected without mutation; recovery continues via the reversal result.
            default:
                return Invalid(nameof(CompleteSuccess));
        }
    }

    /// <summary>
    /// PS-03 / PS-06: definitive failure. Before processing (RECEIVED/VALIDATED) or after a definitive provider
    /// decline / proven non-processing (PROCESSING/PENDING/IN_DOUBT). An active reservation is released (OP-04/OP-07).
    /// Never use this for a timeout: timeouts are <see cref="MarkInDoubt"/>.
    /// </summary>
    public Result<TransitionOutcome> CompleteFailure(TransitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        switch (ProcessingStatus)
        {
            case ProcessingStatus.Received or ProcessingStatus.Validated
                or ProcessingStatus.Processing or ProcessingStatus.Pending or ProcessingStatus.InDoubt:
                return FinancialStatus switch
                {
                    FinancialStatus.None => Apply(context, ProcessingStatus.Failed, financial: null, LedgerAction.None),
                    FinancialStatus.Reserved => Apply(context, ProcessingStatus.Failed, FinancialStatus.Released, LedgerAction.Release),

                    // The original payment was already posted before the reversal became ambiguous: a failure report contradicts it.
                    FinancialStatus.ReversalPending => RecordConflict(context),
                    _ => Invalid(nameof(CompleteFailure)),
                };

            case ProcessingStatus.Failed or ProcessingStatus.Reversed:
                return TransitionOutcome.NoChange;

            // §54: SUCCESS + POSTED followed by FAILED is never applied; it becomes a reconciliation exception.
            case ProcessingStatus.Success or ProcessingStatus.RefundPending or ProcessingStatus.PartiallyRefunded
                or ProcessingStatus.Refunded:
                return RecordConflict(context);

            case ProcessingStatus.ReversalPending when FinancialStatus == FinancialStatus.ReversalPending:
                return RecordConflict(context);

            // TODO / Architecture Decision Required (ADR-012): see CompleteSuccess.
            default:
                return Invalid(nameof(CompleteFailure));
        }
    }

    /// <summary>
    /// PS-09: PENDING / IN_DOUBT / SUCCESS → REVERSAL_PENDING.
    /// Not yet posted: financial stays RESERVED (hold). Already posted: POSTED → REVERSAL_PENDING (FS-04),
    /// with no balance movement until the reversal succeeds.
    /// </summary>
    public Result<TransitionOutcome> BeginReversal(TransitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (ProcessingStatus == ProcessingStatus.ReversalPending)
        {
            return TransitionOutcome.NoChange;
        }

        return (ProcessingStatus, FinancialStatus) switch
        {
            (ProcessingStatus.Pending or ProcessingStatus.InDoubt, FinancialStatus.Reserved) =>
                Apply(context, ProcessingStatus.ReversalPending, financial: null, LedgerAction.None),
            (ProcessingStatus.InDoubt, FinancialStatus.ReversalPending) =>
                Apply(context, ProcessingStatus.ReversalPending, financial: null, LedgerAction.None),
            (ProcessingStatus.Success, FinancialStatus.Posted) =>
                Apply(context, ProcessingStatus.ReversalPending, FinancialStatus.ReversalPending, LedgerAction.None),
            _ => Invalid(nameof(BeginReversal)),
        };
    }

    /// <summary>
    /// PS-10: REVERSAL_PENDING → REVERSED. Active reservation: RESERVED → RELEASED via OP-08 (REVERSAL_RELEASE).
    /// Posted payment: REVERSAL_PENDING → REVERSED via OP-09 compensating posting; the original journal is untouched.
    /// </summary>
    public Result<TransitionOutcome> CompleteReversal(TransitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return (ProcessingStatus, FinancialStatus) switch
        {
            (ProcessingStatus.Reversed, _) => TransitionOutcome.NoChange,
            (ProcessingStatus.ReversalPending, FinancialStatus.Reserved) =>
                Apply(context, ProcessingStatus.Reversed, FinancialStatus.Released, LedgerAction.ReversalRelease),
            (ProcessingStatus.ReversalPending, FinancialStatus.ReversalPending) =>
                Apply(context, ProcessingStatus.Reversed, FinancialStatus.Reversed, LedgerAction.CompensatingReversal),
            _ => Invalid(nameof(CompleteReversal)),
        };
    }

    /// <summary>
    /// State Transition Matrix §18 / ADR-003: definitive reversal decline. A decline never makes the original FAILED.
    /// Posted original: back to SUCCESS + POSTED. Unposted original (truth still unknown): back to IN_DOUBT, hold kept.
    /// </summary>
    public Result<TransitionOutcome> DeclineReversal(TransitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!string.Equals(context.ReasonCode, ReasonCodes.ReversalDeclined, StringComparison.Ordinal))
        {
            return RansysError.Validation(
                ErrorCodes.ReasonCodeMismatch, $"Reversal decline requires reason code {ReasonCodes.ReversalDeclined}.", "reasonCode");
        }

        return (ProcessingStatus, FinancialStatus) switch
        {
            (ProcessingStatus.ReversalPending, FinancialStatus.ReversalPending) =>
                Apply(context, ProcessingStatus.Success, FinancialStatus.Posted, LedgerAction.None),
            (ProcessingStatus.ReversalPending, FinancialStatus.Reserved) =>
                Apply(context, ProcessingStatus.InDoubt, financial: null, LedgerAction.None),
            _ => Invalid(nameof(DeclineReversal)),
        };
    }

    // TODO (refund use case): original-side refund summary transitions (PS-11..PS-13) need cumulative refund
    // tracking and the refund fee policy (ADR-010); they are declared in TransactionTransitions but not exposed yet.

    private Result<TransitionOutcome> Apply(
        TransitionContext context, ProcessingStatus processing, FinancialStatus? financial, LedgerAction ledgerAction)
    {
        var changes = new List<StateChange>();
        ChangeProcessing(processing, context, changes);
        if (financial is { } target)
        {
            ChangeFinancial(target, context, changes);
        }

        if (ledgerAction == LedgerAction.Post)
        {
            FinancialPostedAt = context.OccurredAt;
        }

        if (processing is ProcessingStatus.Success or ProcessingStatus.Failed or ProcessingStatus.Reversed)
        {
            CompletedAt = context.OccurredAt;
        }

        return Commit(context, changes, ledgerAction);
    }

    /// <summary>
    /// Contradicting provider result: reconciliation → EXCEPTION, processing and financial truth untouched,
    /// no ledger action (State Transition Matrix §53, §54, §56).
    /// </summary>
    private Result<TransitionOutcome> RecordConflict(TransitionContext context)
    {
        if (ReconciliationStatus == ReconciliationStatus.Exception)
        {
            return new TransitionOutcome(TransitionKind.ConflictRecorded, LedgerAction.None, []);
        }

        var description = $"{context.ReasonCode} contradicts processing={CanonicalCodes.ProcessingStatus.ToCode(ProcessingStatus)}"
            + $" financial={CanonicalCodes.FinancialStatus.ToCode(FinancialStatus)}";
        var conflict = new StateChange(
            StatusDimension.Reconciliation,
            CanonicalCodes.ReconciliationStatus.ToCode(ReconciliationStatus),
            CanonicalCodes.ReconciliationStatus.ToCode(ReconciliationStatus.Exception),
            ReasonCodes.ConflictingProviderResult,
            description.Length > TransitionContext.MaxReasonDescriptionLength
                ? description[..TransitionContext.MaxReasonDescriptionLength]
                : description,
            context.Source,
            context.AttemptId,
            context.OccurredAt);

        EnsureAllowed(TransactionTransitions.Reconciliation, ReconciliationStatus, ReconciliationStatus.Exception);
        ReconciliationStatus = ReconciliationStatus.Exception;
        UpdatedAt = context.OccurredAt;
        _pendingStateChanges.Add(conflict);
        AssertInvariants();
        return new TransitionOutcome(TransitionKind.ConflictRecorded, LedgerAction.None, [conflict]);
    }

    private void ChangeProcessing(ProcessingStatus to, TransitionContext context, List<StateChange> changes)
    {
        EnsureAllowed(TransactionTransitions.Processing, ProcessingStatus, to);
        changes.Add(Change(
            StatusDimension.Processing,
            CanonicalCodes.ProcessingStatus.ToCode(ProcessingStatus),
            CanonicalCodes.ProcessingStatus.ToCode(to),
            context));
        ProcessingStatus = to;
    }

    private void ChangeFinancial(FinancialStatus to, TransitionContext context, List<StateChange> changes)
    {
        EnsureAllowed(TransactionTransitions.Financial, FinancialStatus, to);
        changes.Add(Change(
            StatusDimension.Financial,
            CanonicalCodes.FinancialStatus.ToCode(FinancialStatus),
            CanonicalCodes.FinancialStatus.ToCode(to),
            context));
        FinancialStatus = to;
    }

    private static StateChange Change(StatusDimension dimension, string from, string to, TransitionContext context) =>
        new(dimension, from, to, context.ReasonCode, context.ReasonDescription, context.Source, context.AttemptId, context.OccurredAt);

    private TransitionOutcome Commit(TransitionContext context, List<StateChange> changes, LedgerAction ledgerAction)
    {
        ReasonCode = context.ReasonCode;
        ReasonDescription = context.ReasonDescription;
        ResponseCode = context.ResponseCode ?? ResponseCode;
        UpdatedAt = context.OccurredAt;
        _pendingStateChanges.AddRange(changes);
        AssertInvariants();
        return new TransitionOutcome(TransitionKind.Applied, ledgerAction, changes);
    }

    private RansysError Invalid(string operation) =>
        RansysError.Conflict(
            ErrorCodes.InvalidStateTransition,
            $"{operation} is not allowed for transaction {Id} in processing={CanonicalCodes.ProcessingStatus.ToCode(ProcessingStatus)}"
            + $" financial={CanonicalCodes.FinancialStatus.ToCode(FinancialStatus)}.");

    private static void EnsureAllowed<TStatus>(TransitionTable<TStatus> table, TStatus from, TStatus to)
        where TStatus : struct, Enum
    {
        if (!table.IsAllowed(from, to))
        {
            throw new InvalidOperationException($"Transition {typeof(TStatus).Name} {from} -> {to} is not in the State Transition Matrix.");
        }
    }

    /// <summary>
    /// Cross-dimension invariants (Canonical Data Model §104). A violation is a programming error.
    /// </summary>
    private void AssertInvariants()
    {
        if (!InvariantsHold())
        {
            throw new InvalidOperationException(
                $"Transaction {Id} violates aggregate invariants: processing={ProcessingStatus} financial={FinancialStatus}.");
        }
    }

    private bool InvariantsHold()
    {
        var financialOk = !RequiresReservation
            ? FinancialStatus == FinancialStatus.None
            : ProcessingStatus switch
            {
                ProcessingStatus.Received => FinancialStatus == FinancialStatus.None,
                ProcessingStatus.Validated => FinancialStatus is FinancialStatus.None or FinancialStatus.Reserved,
                ProcessingStatus.Processing or ProcessingStatus.Pending => FinancialStatus == FinancialStatus.Reserved,
                ProcessingStatus.InDoubt or ProcessingStatus.ReversalPending =>
                    FinancialStatus is FinancialStatus.Reserved or FinancialStatus.ReversalPending,
                ProcessingStatus.Success => FinancialStatus == FinancialStatus.Posted,
                ProcessingStatus.Failed => FinancialStatus is FinancialStatus.None or FinancialStatus.Released,
                ProcessingStatus.Reversed => FinancialStatus is FinancialStatus.Released or FinancialStatus.Reversed,
                _ => true,
            };

        var reserveOk = FinancialStatus == FinancialStatus.None || ReserveAmount is not null;

        return financialOk && reserveOk;
    }
}
