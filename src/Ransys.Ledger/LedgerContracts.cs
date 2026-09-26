using Ransys.Application;
using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Ledger;
using Ransys.Domain.Monetary;

namespace Ransys.Ledger;

/// <summary>
/// Controlled entry point for every balance mutation (Ledger Posting Rule Matrix §35, main.md §14).
/// Each operation runs inside the caller's <see cref="IDatabaseSession"/> so the transaction state change,
/// reservation, balanced journal, wallet projection and outbox event commit atomically. Operations are
/// idempotent by <see cref="PostingKey"/>: a repeated call returns <see cref="LedgerPostingOutcome.AlreadyPosted"/>
/// (POSTING_ALREADY_EXISTS semantics) and never mutates balances twice.
/// <para>
/// Lock order (ERD v1.1 §23): the caller locks the transaction row first; this service then locks the wallet,
/// then the reservation, then checks the posting key. No network I/O may happen inside the session.
/// </para>
/// </summary>
public interface ILedgerPostingService
{
    /// <summary>OP-02: available → reserved for principal + guaranteed fee.</summary>
    Task<Result<LedgerPostingResult>> ReserveAsync(IDatabaseSession session, ReserveRequest request, CancellationToken cancellationToken = default);

    /// <summary>OP-03 / OP-06: consume the reservation and credit provider payable / revenue / tax.</summary>
    Task<Result<LedgerPostingResult>> PostPaymentAsync(IDatabaseSession session, PostPaymentRequest request, CancellationToken cancellationToken = default);

    /// <summary>OP-04 / OP-07, or OP-08 when <see cref="ReleaseRequest.AsReversal"/> is set.</summary>
    Task<Result<LedgerPostingResult>> ReleaseReservationAsync(IDatabaseSession session, ReleaseRequest request, CancellationToken cancellationToken = default);

    /// <summary>OP-09: compensating posting for a posted payment; the original journal is never modified.</summary>
    Task<Result<LedgerPostingResult>> ReversePostedPaymentAsync(IDatabaseSession session, ReversePostedPaymentRequest request, CancellationToken cancellationToken = default);

    /// <summary>OP-01.</summary>
    Task<Result<LedgerPostingResult>> PostTopUpAsync(IDatabaseSession session, TopUpRequest request, CancellationToken cancellationToken = default);

    /// <summary>OP-10 / OP-11 / OP-12.</summary>
    Task<Result<LedgerPostingResult>> PostRefundAsync(IDatabaseSession session, RefundRequest request, CancellationToken cancellationToken = default);

    /// <summary>OP-13.</summary>
    Task<Result<LedgerPostingResult>> CreditAdjustmentAsync(IDatabaseSession session, AdjustmentRequest request, CancellationToken cancellationToken = default);

    /// <summary>OP-14: never creates a negative balance.</summary>
    Task<Result<LedgerPostingResult>> DebitAdjustmentAsync(IDatabaseSession session, AdjustmentRequest request, CancellationToken cancellationToken = default);

    /// <summary>OP-05: no posting; the reservation stays ACTIVE with an updated hold reason (e.g. IN_DOUBT).</summary>
    Task<Result> ChangeHoldReasonAsync(IDatabaseSession session, TransactionId transactionId, string holdReason, CancellationToken cancellationToken = default);
}

public sealed record ReserveRequest(
    TransactionId TransactionId,
    WalletId WalletId,
    Money Principal,
    Money Fee,
    string HoldReason);

/// <summary>
/// Credit side of a payment success posting. Must sum to the reservation total.
/// </summary>
public sealed record PaymentSplit(Money ProviderPayable, Money FeeRevenue, Money TaxPayable)
{
    /// <summary>
    /// Baseline OP-03 split: principal to provider payable, merchant fee to RANSYS revenue.
    /// TODO / Architecture Decision Required: mapping fee components' accounting amounts to provider cost, tax
    /// and margin accounts (Ledger Posting Rule Matrix §13) is not specified; callers may pass an explicit split.
    /// </summary>
    public static PaymentSplit Baseline(Money principal, Money fee) => new(principal, fee, Money.Zero(principal.Currency));
}

public sealed record PostPaymentRequest(TransactionId TransactionId, ProviderId ProviderId, PaymentSplit? Split = null);

public sealed record ReleaseRequest(TransactionId TransactionId, bool AsReversal = false);

public sealed record ReversePostedPaymentRequest(TransactionId TransactionId, string ReversalReference);

public sealed record TopUpRequest(
    WalletId WalletId,
    string TopUpReference,
    Money Amount,
    Guid? ApprovalRequestId,
    LedgerActor Actor);

/// <summary>
/// Who authorized a refund posting (ADR-024). Exactly one of two explicit forms; there is no "unauthorized" value.
/// </summary>
public abstract record RefundAuthorization
{
    private RefundAuthorization()
    {
    }

    /// <summary>
    /// Manual (Backoffice) refund: an approved maker-checker request (Architecture Spec §31). The approval id is
    /// written to the journal.
    /// </summary>
    public sealed record ApprovedRequest(Guid ApprovalRequestId) : RefundAuthorization;

    /// <summary>
    /// Refund requested by an authenticated merchant through the API and executed as a REFUND child transaction that
    /// the provider confirmed (ADR-023). It is bound to that child: <see cref="RefundRequest.RefundTransactionId"/>
    /// must equal <see cref="RefundTransactionId"/>. The journal has no approval id; the evidence is the child
    /// transaction (authenticated channel, client reference, attempts and history).
    /// </summary>
    public sealed record MerchantApiRequest(TransactionId RefundTransactionId, ChannelId ChannelId, string ClientReference)
        : RefundAuthorization;
}

/// <summary>
/// Refund of a posted payment. <see cref="Principal"/> debits the provider receivable and <see cref="Fee"/>
/// reverses fee revenue; the fee-refund policy itself is decided by the refund use case (ADR-010).
/// </summary>
public sealed record RefundRequest(
    TransactionId OriginalTransactionId,
    TransactionId? RefundTransactionId,
    string RefundReference,
    ProviderId ProviderId,
    Money Principal,
    Money Fee,
    RefundAuthorization Authorization,
    LedgerActor Actor)
{
    /// <summary>Manual refund with a maker-checker approval (<see cref="RefundAuthorization.ApprovedRequest"/>).</summary>
    public RefundRequest(
        TransactionId originalTransactionId,
        TransactionId? refundTransactionId,
        string refundReference,
        ProviderId providerId,
        Money principal,
        Money fee,
        Guid approvalRequestId,
        LedgerActor actor)
        : this(
            originalTransactionId, refundTransactionId, refundReference, providerId, principal, fee,
            new RefundAuthorization.ApprovedRequest(approvalRequestId), actor)
    {
    }

    /// <summary>The maker-checker approval id; null for a merchant API refund (ADR-024).</summary>
    public Guid? ApprovalRequestId => (Authorization as RefundAuthorization.ApprovedRequest)?.ApprovalRequestId;
}

/// <summary>Manual adjustment; requires reason, evidence and an approved maker-checker request (OP-13/14, §43).</summary>
public sealed record AdjustmentRequest(
    WalletId WalletId,
    string AdjustmentReference,
    Money Amount,
    string Reason,
    string SupportingReference,
    Guid ApprovalRequestId,
    LedgerActor Actor);

public enum LedgerPostingOutcome
{
    Posted,

    /// <summary>The posting key already existed with identical terms; nothing was changed.</summary>
    AlreadyPosted,
}

public sealed record WalletBalances(WalletId WalletId, Money Ledger, Money Available, Money Reserved)
{
    public static WalletBalances Of(Wallet wallet) =>
        new(wallet.Id, wallet.LedgerBalance, wallet.AvailableBalance, wallet.ReservedBalance);
}

public sealed record LedgerPostingResult(
    LedgerPostingOutcome Outcome,
    PostingKey PostingKey,
    Guid LedgerTransactionId,
    WalletBalances Balances);

/// <summary>Summary of an existing journal, used for idempotency and compensation checks.</summary>
public sealed record PostedJournalSummary(Guid Id, LedgerOperationType OperationType, Money Total);

/// <summary>Persistence port of the Ledger Posting Service (implemented with PostgreSQL row locks).</summary>
public interface ILedgerStore
{
    Task<WalletId?> FindReservationWalletAsync(IDatabaseSession session, TransactionId transactionId, CancellationToken cancellationToken);

    /// <summary><c>SELECT … FOR UPDATE</c> on the wallet row.</summary>
    Task<Result<Wallet?>> LockWalletAsync(IDatabaseSession session, WalletId walletId, CancellationToken cancellationToken);

    /// <summary><c>SELECT … FOR UPDATE</c> on the transaction's reservation row.</summary>
    Task<Result<Reservation?>> LockReservationAsync(IDatabaseSession session, TransactionId transactionId, CancellationToken cancellationToken);

    Task<Result<PostedJournalSummary?>> FindJournalAsync(IDatabaseSession session, PostingKey postingKey, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<JournalLine>>> GetJournalLinesAsync(IDatabaseSession session, Guid journalId, CancellationToken cancellationToken);

    Task<bool> IsCompensatedAsync(IDatabaseSession session, Guid journalId, CancellationToken cancellationToken);

    /// <summary>Sum of credit entries to <paramref name="accountCode"/> in journals whose posting key starts with the prefix.</summary>
    Task<decimal> SumCreditsAsync(IDatabaseSession session, string postingKeyPrefix, string accountCode, CancellationToken cancellationToken);

    Task InsertReservationAsync(IDatabaseSession session, Reservation reservation, CancellationToken cancellationToken);

    Task UpdateReservationAsync(IDatabaseSession session, Reservation reservation, CancellationToken cancellationToken);

    /// <summary>Inserts the journal and its entries, creating missing accounts idempotently.</summary>
    Task<Result> InsertJournalAsync(IDatabaseSession session, Journal journal, CancellationToken cancellationToken);

    /// <summary>Writes the wallet projection and status; the row is already locked by <see cref="LockWalletAsync"/>.</summary>
    Task UpdateWalletAsync(IDatabaseSession session, Wallet wallet, CancellationToken cancellationToken);

    /// <summary>
    /// True if the wallet has an ACTIVE reservation, or a transaction (or a child of one) reserved on it that is not
    /// financially resolved yet (ADR-016 close precondition).
    /// </summary>
    Task<bool> HasUnresolvedFinancialActivityAsync(IDatabaseSession session, WalletId walletId, CancellationToken cancellationToken);
}
