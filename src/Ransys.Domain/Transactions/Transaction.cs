using Ransys.Domain.Attempts;
using Ransys.Domain.Common;
using Ransys.Domain.Fees;
using Ransys.Domain.Monetary;
using Ransys.Domain.Routing;

namespace Ransys.Domain.Transactions;

/// <summary>
/// The most recent provider evidence reported for this transaction from any source (ADR-027), separate from the
/// immutable per-attempt outcome (ADR-005). Always overwritable: a later report replaces an earlier one so GET,
/// replay and child requests can see the latest business-facing reference/data even after the attempt outcome that
/// first resolved the transaction is already immutable.
/// </summary>
public sealed record TransactionResultProjection(AttemptOutcome Evidence, ChangeSource Source, DateTimeOffset RecordedAt);

/// <summary>
/// Outcome of <see cref="Transaction.MergeLatestProviderResult"/> (ADR-027 revised, U1): tells the caller whether the
/// accepted-evidence projection changed and how, so a genuinely conflicting later report can be observed instead of
/// silently applied.
/// </summary>
public enum ProviderResultMergeOutcome
{
    /// <summary>No projection existed yet; the incoming evidence became the projection.</summary>
    Recorded,

    /// <summary>Same identity (or no identity established yet); the projection was filled in / enriched.</summary>
    Enriched,

    /// <summary>The incoming evidence carries no provider identity (reference/STAN/RRN) to merge or compare by.</summary>
    IgnoredNoIdentity,

    /// <summary>The incoming evidence's provider identity differs from the already-accepted one; nothing changed.</summary>
    ConflictingIdentity,
}

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

    /// <summary>
    /// ADR-027: the latest provider evidence reported for this transaction, from any source. A plain overwritable
    /// projection, not a status dimension: changing it produces no <see cref="StateChange"/> / history row.
    /// </summary>
    public TransactionResultProjection? LatestProviderResult { get; private set; }

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
            LatestProviderResult = snapshot.LatestProviderResult,
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
                ErrorCodes.OriginalTransactionRequired, "Refund, reversal and void transactions must reference the original transaction.", "originalTransactionId");
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

    /// <summary>
    /// ADR-027 (RR3): a genuinely new accepted transition (<see cref="TransitionKind.Applied"/>) unconditionally
    /// replaces the accepted-evidence projection with this report's evidence. This is deliberately <em>not</em>
    /// merged against whatever was recorded before: an <c>Applied</c> transition means the transaction's true status
    /// just moved (e.g. a temporary PENDING report superseded by the real final SUCCESS/FAILED one, ADR-027's own
    /// T2-B scenario), so the evidence describing the arrival at that new status is authoritative for it, even when
    /// its own provider reference/STAN/RRN legitimately differs from the now-superseded prior status's evidence (a
    /// provisional reference is not "the same identity" as the final one). Contrast
    /// <see cref="MergeLatestProviderResult"/>, used for a duplicate/late report of a status that was <em>already</em>
    /// accepted (<see cref="TransitionKind.NoChange"/>), where a differing identity is a genuine U1 conflict, not a
    /// state supersession, and must not be silently swapped in. Does not touch
    /// processing/financial/reconciliation/settlement status and produces no <see cref="StateChange"/>.
    /// </summary>
    public void RecordLatestProviderResult(AttemptOutcome evidence, ChangeSource source, DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        LatestProviderResult = new TransactionResultProjection(evidence, source, recordedAt);
    }

    /// <summary>
    /// ADR-027 (revised, U1/V1): merges a new provider result into the accepted-evidence projection instead of
    /// blindly overwriting it, for a duplicate/late report of a status that is <em>already</em> accepted
    /// (<see cref="TransitionKind.NoChange"/>) — never for a genuinely new transition, see
    /// <see cref="RecordLatestProviderResult"/>. <see cref="AttemptOutcome.ProviderReference"/>,
    /// <see cref="AttemptOutcome.ProviderStan"/> and <see cref="AttemptOutcome.ProviderRrn"/> are three distinct
    /// identity fields, never collapsed into one via <c>??</c>: each is compared independently against its own
    /// existing value, per <see cref="MergeIdentityField"/>'s decision table (applied to each field on its own):
    /// <list type="bullet">
    /// <item>existing null, incoming null → stays null;</item>
    /// <item>existing null, incoming non-null → enriches (the incoming value is taken);</item>
    /// <item>existing non-null, incoming null → keeps the existing value (a blank incoming field never erases it);</item>
    /// <item>existing non-null, incoming non-null and equal → keeps it (no-op);</item>
    /// <item>existing non-null, incoming non-null and different → conflict on that field.</item>
    /// </list>
    /// A conflict on <em>any</em> one of the three fields makes the whole report <see cref="ProviderResultMergeOutcome.ConflictingIdentity"/>
    /// and changes nothing; there is no cross-field fallback (a shared RRN does not excuse a different STAN, and vice
    /// versa). Otherwise the fields merge and non-identity fields fill in blanks the same way as before; non-empty
    /// <see cref="AttemptOutcome.Data"/> replaces the existing payload as a whole (a snapshot, not a key-by-key
    /// patch — see the inline comment below) rather than being spliced key-by-key. The caller can observe a conflict
    /// through the returned <see cref="ProviderResultMergeOutcome"/> instead of it being silently applied or dropped.
    /// Does not touch processing/financial/reconciliation/settlement status and produces no <see cref="StateChange"/>.
    /// </summary>
    public ProviderResultMergeOutcome MergeLatestProviderResult(AttemptOutcome incoming, ChangeSource source, DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        var existing = LatestProviderResult?.Evidence;
        if (existing is null)
        {
            LatestProviderResult = new TransactionResultProjection(incoming, source, recordedAt);
            return ProviderResultMergeOutcome.Recorded;
        }

        if (incoming.ProviderReference is null && incoming.ProviderStan is null && incoming.ProviderRrn is null
            && incoming.Data is not { Count: > 0 })
        {
            return ProviderResultMergeOutcome.IgnoredNoIdentity;
        }

        var (reference, referenceConflict) = MergeIdentityField(existing.ProviderReference, incoming.ProviderReference);
        var (stan, stanConflict) = MergeIdentityField(existing.ProviderStan, incoming.ProviderStan);
        var (rrn, rrnConflict) = MergeIdentityField(existing.ProviderRrn, incoming.ProviderRrn);

        if (referenceConflict || stanConflict || rrnConflict)
        {
            return ProviderResultMergeOutcome.ConflictingIdentity;
        }

        var merged = AttemptOutcome.Create(
            requestSent: incoming.RequestSent,
            transportStatus: incoming.TransportStatus,
            providerTransactionStatus: incoming.ProviderTransactionStatus ?? existing.ProviderTransactionStatus,
            ransysResponseCode: incoming.RansysResponseCode ?? existing.RansysResponseCode,
            providerResponseCode: incoming.ProviderResponseCode ?? existing.ProviderResponseCode,
            providerResponseMessage: incoming.ProviderResponseMessage ?? existing.ProviderResponseMessage,
            providerReference: reference,
            providerStan: stan,
            providerRrn: rrn,
            latency: incoming.Latency ?? existing.Latency,
            rawMessages: incoming.RawMessages != RawMessageReferences.None ? incoming.RawMessages : existing.RawMessages,
            providerSentAt: incoming.ProviderSentAt ?? existing.ProviderSentAt,
            providerResponseAt: incoming.ProviderResponseAt ?? existing.ProviderResponseAt,
            metadata: incoming.Metadata,
            // Snapshot semantics, chosen explicitly (not a key-by-key patch): a fresher non-empty report replaces the
            // whole payload, since splicing keys from two different reports' snapshots could silently mix incompatible data.
            data: incoming.Data is { Count: > 0 } ? incoming.Data : existing.Data);

        if (merged.IsFailure)
        {
            // Every field came from an already-validated AttemptOutcome, so this should be unreachable. If it somehow
            // isn't, treat it as needing attention (observable via V2's conflict path) rather than silently discarding it.
            return ProviderResultMergeOutcome.ConflictingIdentity;
        }

        LatestProviderResult = new TransactionResultProjection(merged.Value, source, recordedAt);
        return ProviderResultMergeOutcome.Enriched;
    }

    /// <summary>
    /// Per-field identity merge rule used by <see cref="MergeLatestProviderResult"/> (V1): a blank incoming value
    /// never erases an existing one; a non-null incoming value fills a blank existing one; equal non-null values are
    /// a no-op; different non-null values are a conflict on that field alone.
    /// </summary>
    private static (string? Value, bool Conflict) MergeIdentityField(string? existing, string? incoming)
    {
        if (incoming is null)
        {
            return (existing, false);
        }

        if (existing is null || existing == incoming)
        {
            return (incoming, false);
        }

        return (existing, true);
    }

    /// <summary>
    /// V2 (ADR-027 revised): a provider result whose reference/STAN/RRN conflicts with the already-accepted evidence
    /// must never be silently dropped. Reuses the same reconciliation-exception mechanism a status-level conflict
    /// already uses (idempotent when already EXCEPTION) instead of a separate conflict log.
    /// </summary>
    public Result<TransitionOutcome> RecordProviderEvidenceConflict(TransitionContext context) =>
        RecordReconciliationException(
            ReasonCodes.ConflictingProviderEvidence,
            "A provider result's reference/STAN/RRN conflicts with the already-accepted evidence.",
            context);

    /// <summary>
    /// Decides whether a new provider attempt may be created (ADR-005, ADR-012, ADR-019, State Transition Matrix §15, §64–66):
    /// <list type="bullet">
    /// <item>attempts always target the current routed provider (other providers only via <see cref="RecordFailover"/>);</item>
    /// <item>a primary request must match the transaction type (see <see cref="PrimaryAttemptTypeFor"/>): e.g. REVERSAL
    /// attempts belong to a REVERSAL child transaction (ADR-012), never to the original;</item>
    /// <item>a primary request is allowed only while PROCESSING; for the financial requests (PAYMENT, TRANSFER, REFUND,
    /// REVERSAL, VOID) only if every earlier financial request provably never left RANSYS: no hidden retry after a
    /// possible send;</item>
    /// <item>STATUS_CHECK/ADVICE are allowed while the outcome is open (PROCESSING/PENDING/IN_DOUBT).</item>
    /// </list>
    /// </summary>
    public Result AuthorizeAttempt(AttemptType attemptType, ProviderReference provider, IReadOnlyCollection<TransactionAttempt> priorAttempts)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(priorAttempts);

        if (Routing is null)
        {
            return AttemptNotAllowed(attemptType, "the transaction has no routing decision");
        }

        if (provider.ProviderId != Routing.CurrentProvider.ProviderId)
        {
            return AttemptNotAllowed(attemptType, "attempts must target the current routed provider");
        }

        if (priorAttempts.Any(a => a.TransactionId != Id))
        {
            throw new ArgumentException("Prior attempts must belong to this transaction.", nameof(priorAttempts));
        }

        switch (attemptType)
        {
            case AttemptType.Payment or AttemptType.Transfer or AttemptType.Refund or AttemptType.Reversal or AttemptType.Void
                or AttemptType.Inquiry or AttemptType.BalanceInquiry:
                if (PrimaryAttemptTypeFor(Type) != attemptType)
                {
                    return AttemptNotAllowed(
                        attemptType,
                        $"the primary request of a {CanonicalCodes.TransactionType.ToCode(Type)} transaction is not"
                        + $" {CanonicalCodes.AttemptType.ToCode(attemptType)} (a reversal is sent by a REVERSAL child transaction, ADR-012)");
                }

                if (ProcessingStatus != ProcessingStatus.Processing)
                {
                    return AttemptNotAllowed(attemptType, "a primary request can only be sent while PROCESSING");
                }

                if (!IsFinancialRequest(attemptType))
                {
                    return Result.Success();
                }

                var possiblySent = priorAttempts.FirstOrDefault(a => IsFinancialRequest(a.AttemptType) && a.MayHaveReachedProvider);
                return possiblySent is null
                    ? Result.Success()
                    : RansysError.Financial(
                        ErrorCodes.FailoverNotAllowed,
                        $"Attempt {possiblySent.AttemptNumber} may have reached the provider; a new request is not allowed (IN_DOUBT handling applies).");

            case AttemptType.StatusCheck or AttemptType.Advice:
                return ProcessingStatus is ProcessingStatus.Processing or ProcessingStatus.Pending or ProcessingStatus.InDoubt
                    ? Result.Success()
                    : AttemptNotAllowed(attemptType, "the transaction outcome is already final");

            default:
                return AttemptNotAllowed(attemptType, "unknown attempt type");
        }
    }

    /// <summary>
    /// The attempt type of this transaction type's own provider request; null when the type has no primary request
    /// (advice, status check and internal flows).
    /// </summary>
    public static AttemptType? PrimaryAttemptTypeFor(TransactionType type) => type switch
    {
        TransactionType.Payment or TransactionType.Purchase => AttemptType.Payment,
        TransactionType.Transfer => AttemptType.Transfer,
        TransactionType.Refund => AttemptType.Refund,
        TransactionType.Reversal => AttemptType.Reversal,
        TransactionType.Void => AttemptType.Void,
        TransactionType.Inquiry => AttemptType.Inquiry,
        TransactionType.BalanceInquiry => AttemptType.BalanceInquiry,
        _ => null,
    };

    /// <summary>Requests that may move money at the provider: never resent after a possible send (ADR-005).</summary>
    private static bool IsFinancialRequest(AttemptType attemptType) =>
        attemptType is AttemptType.Payment or AttemptType.Transfer or AttemptType.Refund or AttemptType.Reversal or AttemptType.Void;

    private RansysError AttemptNotAllowed(AttemptType attemptType, string reason) =>
        RansysError.Conflict(
            ErrorCodes.AttemptNotAllowed,
            $"{attemptType} attempt not allowed for transaction {Id} (processing={CanonicalCodes.ProcessingStatus.ToCode(ProcessingStatus)}): {reason}.");

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
    /// PS-08: PROCESSING / PENDING → IN_DOUBT when the final provider result cannot be proven.
    /// Financial status is preserved: the reservation is never released automatically (OP-05).
    /// </summary>
    public Result<TransitionOutcome> MarkInDoubt(TransitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return ProcessingStatus switch
        {
            ProcessingStatus.InDoubt => TransitionOutcome.NoChange,
            ProcessingStatus.Processing or ProcessingStatus.Pending =>
                Apply(context, ProcessingStatus.InDoubt, financial: null, LedgerAction.None),
            _ => Invalid(nameof(MarkInDoubt)),
        };
    }

    /// <summary>
    /// PS-05: definitive provider SUCCESS of this transaction's own request, from PROCESSING / PENDING / IN_DOUBT.
    /// Reserving transactions move RESERVED → POSTED and require OP-03 (POST). A pending reversal child does not
    /// block it (ADR-012).
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

                return FinancialStatus == FinancialStatus.Reserved
                    ? Apply(context, ProcessingStatus.Success, FinancialStatus.Posted, LedgerAction.Post)
                    : Invalid(nameof(CompleteSuccess));

            // Success already established, or later reversed/refunded consistently: duplicate/late report.
            case ProcessingStatus.Success or ProcessingStatus.RefundPending or ProcessingStatus.PartiallyRefunded
                or ProcessingStatus.Refunded or ProcessingStatus.Reversed:
                return TransitionOutcome.NoChange;

            // §53: FAILED was assigned only on definitive proof; a later SUCCESS is a contradiction, never FAILED → SUCCESS.
            case ProcessingStatus.Failed:
                return RecordConflict(context);

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
                    _ => Invalid(nameof(CompleteFailure)),
                };

            case ProcessingStatus.Failed or ProcessingStatus.Reversed:
                return TransitionOutcome.NoChange;

            // §54: SUCCESS + POSTED followed by FAILED is never applied; it becomes a reconciliation exception.
            case ProcessingStatus.Success or ProcessingStatus.RefundPending or ProcessingStatus.PartiallyRefunded
                or ProcessingStatus.Refunded:
                return RecordConflict(context);

            default:
                return Invalid(nameof(CompleteFailure));
        }
    }

    /// <summary>
    /// ADR-012: whether a REVERSAL child transaction may be started for this (original) transaction. Reversible are
    /// reserving transactions that are PENDING / IN_DOUBT with an active reservation, or SUCCESS and POSTED
    /// (State Transition Matrix PS-09). Starting the reversal changes nothing on the original.
    /// </summary>
    public Result AuthorizeReversal()
    {
        var reversible = RequiresReservation && (ProcessingStatus, FinancialStatus) is
            (ProcessingStatus.Pending or ProcessingStatus.InDoubt, FinancialStatus.Reserved)
            or (ProcessingStatus.Success, FinancialStatus.Posted);

        return reversible
            ? Result.Success()
            : RansysError.Conflict(
                ErrorCodes.ReversalNotAllowed,
                $"Transaction {Id} cannot be reversed in processing={CanonicalCodes.ProcessingStatus.ToCode(ProcessingStatus)}"
                + $" financial={CanonicalCodes.FinancialStatus.ToCode(FinancialStatus)}.");
    }

    /// <summary>
    /// ADR-012: the REVERSAL child <paramref name="reversalTransactionId"/> was confirmed by the provider. The financial
    /// effect follows this transaction's financial state at this moment (PS-10):
    /// <list type="bullet">
    /// <item>active reservation (PENDING / IN_DOUBT, RESERVED) → REVERSED + RELEASED via OP-08 (REVERSAL_RELEASE);</item>
    /// <item>posted (SUCCESS, POSTED) → REVERSED + REVERSED via OP-09 compensating posting; the original journal is untouched;</item>
    /// <item>already REVERSED, or FAILED (nothing was ever consumed) → no change, no money moves.</item>
    /// </list>
    /// </summary>
    public Result<TransitionOutcome> ApplyReversalConfirmed(TransactionId reversalTransactionId, TransitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (reversalTransactionId == Id)
        {
            return RansysError.Validation(ErrorCodes.OriginalTransactionSelfReference, "A transaction cannot reverse itself.", "reversalTransactionId");
        }

        return (ProcessingStatus, FinancialStatus) switch
        {
            (ProcessingStatus.Reversed, _) or (ProcessingStatus.Failed, _) => TransitionOutcome.NoChange,
            (ProcessingStatus.Pending or ProcessingStatus.InDoubt, FinancialStatus.Reserved) when RequiresReservation =>
                Apply(context, ProcessingStatus.Reversed, FinancialStatus.Released, LedgerAction.ReversalRelease),
            (ProcessingStatus.Success, FinancialStatus.Posted) when RequiresReservation =>
                Apply(context, ProcessingStatus.Reversed, FinancialStatus.Reversed, LedgerAction.CompensatingReversal),
            _ => Invalid(nameof(ApplyReversalConfirmed)),
        };
    }

    /// <summary>
    /// ADR-023: whether a REFUND child transaction may be started for this (original) transaction. Refundable are
    /// reserving transactions that are SUCCESS + POSTED or PARTIALLY_REFUNDED + PARTIALLY_REFUNDED. Starting the refund
    /// changes nothing on the original (no REFUND_PENDING). The amount limit (cumulative refunds never exceed the posted
    /// amount) is enforced by the Ledger Posting Service.
    /// </summary>
    public Result AuthorizeRefund()
    {
        var refundable = RequiresReservation && (ProcessingStatus, FinancialStatus) is
            (ProcessingStatus.Success, FinancialStatus.Posted)
            or (ProcessingStatus.PartiallyRefunded, FinancialStatus.PartiallyRefunded);

        return refundable
            ? Result.Success()
            : RansysError.Conflict(
                ErrorCodes.RefundNotAllowed,
                $"Transaction {Id} cannot be refunded in processing={CanonicalCodes.ProcessingStatus.ToCode(ProcessingStatus)}"
                + $" financial={CanonicalCodes.FinancialStatus.ToCode(FinancialStatus)}.");
    }

    /// <summary>
    /// ADR-023: the REFUND child <paramref name="refundTransactionId"/> succeeded. Its refund posting
    /// (<c>TX:&lt;original&gt;:REFUND:&lt;ref&gt;</c>) is executed separately by the caller in the same database
    /// transaction, so this always returns <see cref="LedgerAction.None"/>.
    /// <list type="bullet">
    /// <item>SUCCESS + POSTED → PARTIALLY_REFUNDED or REFUNDED (both dimensions);</item>
    /// <item>PARTIALLY_REFUNDED → REFUNDED when <paramref name="fullyRefunded"/>; otherwise no status change;</item>
    /// <item>already REFUNDED → no change; anything else is an invalid transition.</item>
    /// </list>
    /// </summary>
    public Result<TransitionOutcome> ApplyRefundCompleted(TransactionId refundTransactionId, bool fullyRefunded, TransitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (refundTransactionId == Id)
        {
            return RansysError.Validation(ErrorCodes.OriginalTransactionSelfReference, "A transaction cannot refund itself.", "refundTransactionId");
        }

        if (!RequiresReservation)
        {
            return Invalid(nameof(ApplyRefundCompleted));
        }

        return (ProcessingStatus, FinancialStatus, fullyRefunded) switch
        {
            (ProcessingStatus.Refunded, FinancialStatus.Refunded, _) => TransitionOutcome.NoChange,
            (ProcessingStatus.PartiallyRefunded, FinancialStatus.PartiallyRefunded, false) => TransitionOutcome.NoChange,
            (ProcessingStatus.Success, FinancialStatus.Posted, false) =>
                Apply(context, ProcessingStatus.PartiallyRefunded, FinancialStatus.PartiallyRefunded, LedgerAction.None),
            (ProcessingStatus.Success, FinancialStatus.Posted, true)
                or (ProcessingStatus.PartiallyRefunded, FinancialStatus.PartiallyRefunded, true) =>
                Apply(context, ProcessingStatus.Refunded, FinancialStatus.Refunded, LedgerAction.None),
            _ => Invalid(nameof(ApplyRefundCompleted)),
        };
    }

    /// <summary>
    /// ADR-019: whether a VOID child transaction may be started for this (original) transaction. Same states as
    /// <see cref="AuthorizeReversal"/>. Starting the void changes nothing on the original.
    /// </summary>
    public Result AuthorizeVoid() =>
        AuthorizeReversal().IsSuccess
            ? Result.Success()
            : RansysError.Conflict(
                ErrorCodes.VoidNotAllowed,
                $"Transaction {Id} cannot be voided in processing={CanonicalCodes.ProcessingStatus.ToCode(ProcessingStatus)}"
                + $" financial={CanonicalCodes.FinancialStatus.ToCode(FinancialStatus)}.");

    /// <summary>
    /// ADR-019 (fail closed): the VOID child <paramref name="voidTransactionId"/> was confirmed by the provider. VOID
    /// financial semantics are not decided yet, so processing and financial state never change and no money moves;
    /// reconciliation becomes EXCEPTION (<see cref="ReasonCodes.VoidConfirmedRequiresReview"/>) for manual handling.
    /// Idempotent when reconciliation is already EXCEPTION.
    /// </summary>
    public Result<TransitionOutcome> RecordVoidConfirmed(TransactionId voidTransactionId, TransitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (voidTransactionId == Id)
        {
            return RansysError.Validation(ErrorCodes.OriginalTransactionSelfReference, "A transaction cannot void itself.", "voidTransactionId");
        }

        return RecordReconciliationException(
            ReasonCodes.VoidConfirmedRequiresReview,
            $"VOID child {voidTransactionId} confirmed; VOID financial semantics are pending (ADR-019)",
            context);
    }

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
    private Result<TransitionOutcome> RecordConflict(TransitionContext context) =>
        RecordReconciliationException(
            ReasonCodes.ConflictingProviderResult,
            $"{context.ReasonCode} contradicts processing={CanonicalCodes.ProcessingStatus.ToCode(ProcessingStatus)}"
            + $" financial={CanonicalCodes.FinancialStatus.ToCode(FinancialStatus)}",
            context);

    /// <summary>Reconciliation → EXCEPTION only; idempotent when already EXCEPTION. Never touches processing/financial.</summary>
    private Result<TransitionOutcome> RecordReconciliationException(string reasonCode, string description, TransitionContext context)
    {
        if (ReconciliationStatus == ReconciliationStatus.Exception)
        {
            return new TransitionOutcome(TransitionKind.ConflictRecorded, LedgerAction.None, []);
        }

        var conflict = new StateChange(
            StatusDimension.Reconciliation,
            CanonicalCodes.ReconciliationStatus.ToCode(ReconciliationStatus),
            CanonicalCodes.ReconciliationStatus.ToCode(ReconciliationStatus.Exception),
            reasonCode,
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
                ProcessingStatus.InDoubt => FinancialStatus == FinancialStatus.Reserved,
                ProcessingStatus.Success => FinancialStatus == FinancialStatus.Posted,
                ProcessingStatus.Failed => FinancialStatus is FinancialStatus.None or FinancialStatus.Released,
                ProcessingStatus.Reversed => FinancialStatus is FinancialStatus.Released or FinancialStatus.Reversed,

                // ADR-023: refund summary states move both dimensions together.
                ProcessingStatus.PartiallyRefunded => FinancialStatus == FinancialStatus.PartiallyRefunded,
                ProcessingStatus.Refunded => FinancialStatus == FinancialStatus.Refunded,
                _ => true,
            };

        var reserveOk = FinancialStatus == FinancialStatus.None || ReserveAmount is not null;

        return financialOk && reserveOk;
    }
}
