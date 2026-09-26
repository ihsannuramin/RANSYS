using Ransys.Application;
using Ransys.Contracts.Events;
using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Ledger;
using Ransys.Domain.Monetary;

namespace Ransys.Ledger;

/// <summary>
/// Ledger Posting Service (Ledger Posting Rule Matrix §35). Business rules live here and in the domain;
/// PostgreSQL enforces the final integrity (balance trigger, immutability, unique posting key).
/// </summary>
public sealed class LedgerPostingService : ILedgerPostingService
{
    private readonly ILedgerStore _store;
    private readonly IOutboxWriter _outbox;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;

    public LedgerPostingService(ILedgerStore store, IOutboxWriter outbox, IClock clock, IIdGenerator ids)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
    }

    public async Task<Result<LedgerPostingResult>> ReserveAsync(
        IDatabaseSession session, ReserveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = PostingKey.Reserve(request.TransactionId);
        var total = request.Principal.Currency == request.Fee.Currency
            ? request.Principal.Add(request.Fee)
            : RansysError.Validation(ErrorCodes.CurrencyMismatch, "Principal and fee must share a currency definition.", "fee");
        if (total.IsFailure)
        {
            return total.Error;
        }

        var wallet = await LockWallet(session, request.WalletId, cancellationToken);
        if (wallet.IsFailure)
        {
            return wallet.Error;
        }

        var existing = await IdempotentResult(session, key, LedgerOperationType.Reserve, total.Value, wallet.Value, cancellationToken);
        if (existing is not null)
        {
            return existing.Value;
        }

        var reservationLock = await _store.LockReservationAsync(session, request.TransactionId, cancellationToken);
        if (reservationLock.IsFailure)
        {
            return reservationLock.Error;
        }

        if (reservationLock.Value is not null)
        {
            return RansysError.Conflict(
                ErrorCodes.ReservationAlreadyExists, $"Transaction {request.TransactionId} already has a reservation.");
        }

        var now = _clock.UtcNow;
        var reservation = Reservation.Open(
            _ids.NewId(), wallet.Value.Id, request.TransactionId, request.Principal, request.Fee, request.HoldReason, now);
        if (reservation.IsFailure)
        {
            return reservation.Error;
        }

        var reserved = wallet.Value.Reserve(total.Value);
        if (reserved.IsFailure)
        {
            return reserved.Error;
        }

        var journal = Journal.Create(new JournalDraft(
            _ids.NewId(), key, LedgerOperationType.Reserve, request.TransactionId, reservation.Value.Id, null,
            request.TransactionId.ToString(), "Payment reserve", now, now, LedgerActor.System, null,
            [
                new JournalLine(LedgerAccounts.MerchantAvailable(wallet.Value), EntrySide.Debit, total.Value),
                new JournalLine(LedgerAccounts.MerchantReserved(wallet.Value), EntrySide.Credit, total.Value),
            ]));
        if (journal.IsFailure)
        {
            return journal.Error;
        }

        await _store.InsertReservationAsync(session, reservation.Value, cancellationToken);
        return await Persist(session, journal.Value, wallet.Value, LedgerEventTypes.WalletReserved, cancellationToken);
    }

    public async Task<Result<LedgerPostingResult>> PostPaymentAsync(
        IDatabaseSession session, PostPaymentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = PostingKey.Post(request.TransactionId);

        var locked = await LockWalletAndReservation(session, request.TransactionId, cancellationToken);
        if (locked.IsFailure)
        {
            return locked.Error;
        }

        var (wallet, reservation) = locked.Value;
        var existing = await IdempotentResult(session, key, LedgerOperationType.Post, reservation.Total, wallet, cancellationToken);
        if (existing is not null)
        {
            return existing.Value;
        }

        var split = request.Split ?? PaymentSplit.Baseline(reservation.Principal, reservation.Fee);
        var splitTotal = Money.Sum([split.ProviderPayable, split.FeeRevenue, split.TaxPayable], wallet.Currency);
        if (splitTotal.IsFailure || splitTotal.Value != reservation.Total)
        {
            return RansysError.Financial(
                ErrorCodes.PostingAmountMismatch,
                $"Payment split must equal the reservation total {reservation.Total.ToCanonicalAmountString()}.");
        }

        var now = _clock.UtcNow;
        var committed = reservation.Commit(now);
        if (committed.IsFailure)
        {
            return committed.Error;
        }

        var moved = wallet.CommitReserved(reservation.Total);
        if (moved.IsFailure)
        {
            return moved.Error;
        }

        var lines = new List<JournalLine>
        {
            new(LedgerAccounts.MerchantReserved(wallet), EntrySide.Debit, reservation.Total),
        };
        AddIfPositive(lines, LedgerAccounts.ProviderPayable(request.ProviderId, wallet.Currency), EntrySide.Credit, split.ProviderPayable);
        AddIfPositive(lines, LedgerAccounts.FeeRevenue(wallet.Currency), EntrySide.Credit, split.FeeRevenue);
        AddIfPositive(lines, LedgerAccounts.TaxPayable(wallet.Currency), EntrySide.Credit, split.TaxPayable);

        var journal = Journal.Create(new JournalDraft(
            _ids.NewId(), key, LedgerOperationType.Post, request.TransactionId, reservation.Id, null,
            request.TransactionId.ToString(), "Payment success posting", now, now, LedgerActor.System, null, lines));
        if (journal.IsFailure)
        {
            return journal.Error;
        }

        await _store.UpdateReservationAsync(session, reservation, cancellationToken);
        return await Persist(session, journal.Value, wallet, LedgerEventTypes.TransactionPosted, cancellationToken);
    }

    public async Task<Result<LedgerPostingResult>> ReleaseReservationAsync(
        IDatabaseSession session, ReleaseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = request.AsReversal ? PostingKey.ReversalRelease(request.TransactionId) : PostingKey.Release(request.TransactionId);

        var locked = await LockWalletAndReservation(session, request.TransactionId, cancellationToken);
        if (locked.IsFailure)
        {
            return locked.Error;
        }

        var (wallet, reservation) = locked.Value;
        var existing = await IdempotentResult(session, key, LedgerOperationType.Release, reservation.Total, wallet, cancellationToken);
        if (existing is not null)
        {
            return existing.Value;
        }

        var now = _clock.UtcNow;
        var released = reservation.Release(now);
        if (released.IsFailure)
        {
            return released.Error;
        }

        var moved = wallet.ReleaseReserved(reservation.Total);
        if (moved.IsFailure)
        {
            return moved.Error;
        }

        var journal = Journal.Create(new JournalDraft(
            _ids.NewId(), key, LedgerOperationType.Release, request.TransactionId, reservation.Id, null,
            request.TransactionId.ToString(),
            request.AsReversal ? "Reservation released by confirmed reversal" : "Reservation released",
            now, now, LedgerActor.System, null,
            [
                new JournalLine(LedgerAccounts.MerchantReserved(wallet), EntrySide.Debit, reservation.Total),
                new JournalLine(LedgerAccounts.MerchantAvailable(wallet), EntrySide.Credit, reservation.Total),
            ]));
        if (journal.IsFailure)
        {
            return journal.Error;
        }

        await _store.UpdateReservationAsync(session, reservation, cancellationToken);
        return await Persist(session, journal.Value, wallet, LedgerEventTypes.ReservationReleased, cancellationToken);
    }

    public async Task<Result<LedgerPostingResult>> ReversePostedPaymentAsync(
        IDatabaseSession session, ReversePostedPaymentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = PostingKey.Reversal(request.TransactionId, request.ReversalReference);
        if (key.IsFailure)
        {
            return key.Error;
        }

        var wallet = await LockTransactionWallet(session, request.TransactionId, cancellationToken);
        if (wallet.IsFailure)
        {
            return wallet.Error;
        }

        var original = await FindPostedPayment(session, request.TransactionId, cancellationToken);
        if (original.IsFailure)
        {
            return original.Error;
        }

        var existing = await IdempotentResult(session, key.Value, LedgerOperationType.Reversal, original.Value.Total, wallet.Value, cancellationToken);
        if (existing is not null)
        {
            return existing.Value;
        }

        if (await _store.IsCompensatedAsync(session, original.Value.Id, cancellationToken))
        {
            return RansysError.Financial(ErrorCodes.AlreadyReversed, $"Payment of transaction {request.TransactionId} is already reversed.");
        }

        var originalLines = await _store.GetJournalLinesAsync(session, original.Value.Id, cancellationToken);
        if (originalLines.IsFailure)
        {
            return originalLines.Error;
        }

        // OP-09: every original leg is flipped, except that the merchant is credited on AVAILABLE
        // (the reservation was consumed, so funds return to the spendable balance).
        var reservedCode = LedgerAccounts.MerchantReserved(wallet.Value).Code;
        var lines = originalLines.Value
            .Select(l => l.Account.Code == reservedCode
                ? new JournalLine(LedgerAccounts.MerchantAvailable(wallet.Value), EntrySide.Credit, l.Amount)
                : l with { Side = l.Side == EntrySide.Debit ? EntrySide.Credit : EntrySide.Debit })
            .ToList();

        var credited = wallet.Value.CreditAvailable(original.Value.Total);
        if (credited.IsFailure)
        {
            return credited.Error;
        }

        var now = _clock.UtcNow;
        var journal = Journal.Create(new JournalDraft(
            _ids.NewId(), key.Value, LedgerOperationType.Reversal, request.TransactionId, null, null,
            request.ReversalReference, "Compensating reversal of posted payment", now, now, LedgerActor.System,
            original.Value.Id, lines));
        if (journal.IsFailure)
        {
            return journal.Error;
        }

        return await Persist(session, journal.Value, wallet.Value, LedgerEventTypes.ReversalPosted, cancellationToken);
    }

    public async Task<Result<LedgerPostingResult>> PostTopUpAsync(
        IDatabaseSession session, TopUpRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = PostingKey.TopUp(request.TopUpReference);
        if (key.IsFailure)
        {
            return key.Error;
        }

        if (request.Amount.IsZero)
        {
            return RansysError.Validation(ErrorCodes.OutOfRange, "Top-up amount must be greater than zero.", "amount");
        }

        var wallet = await LockWallet(session, request.WalletId, cancellationToken);
        if (wallet.IsFailure)
        {
            return wallet.Error;
        }

        var existing = await IdempotentResult(session, key.Value, LedgerOperationType.TopUp, request.Amount, wallet.Value, cancellationToken);
        if (existing is not null)
        {
            return existing.Value;
        }

        var credited = wallet.Value.CreditAvailable(request.Amount);
        if (credited.IsFailure)
        {
            return credited.Error;
        }

        var now = _clock.UtcNow;
        var journal = Journal.Create(new JournalDraft(
            _ids.NewId(), key.Value, LedgerOperationType.TopUp, null, null, request.ApprovalRequestId,
            request.TopUpReference, "Merchant top-up confirmed", now, now, request.Actor, null,
            [
                new JournalLine(LedgerAccounts.CashClearing(wallet.Value.Currency), EntrySide.Debit, request.Amount),
                new JournalLine(LedgerAccounts.MerchantAvailable(wallet.Value), EntrySide.Credit, request.Amount),
            ]));
        if (journal.IsFailure)
        {
            return journal.Error;
        }

        return await Persist(session, journal.Value, wallet.Value, LedgerEventTypes.TopUpPosted, cancellationToken);
    }

    public async Task<Result<LedgerPostingResult>> PostRefundAsync(
        IDatabaseSession session, RefundRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ApprovalRequestId == Guid.Empty)
        {
            return ApprovalRequired("Refund");
        }

        var key = PostingKey.Refund(request.OriginalTransactionId, request.RefundReference);
        if (key.IsFailure)
        {
            return key.Error;
        }

        var total = request.Principal.Currency == request.Fee.Currency
            ? request.Principal.Add(request.Fee)
            : RansysError.Validation(ErrorCodes.CurrencyMismatch, "Principal and fee must share a currency definition.", "fee");
        if (total.IsFailure)
        {
            return total.Error;
        }

        if (total.Value.IsZero)
        {
            return RansysError.Validation(ErrorCodes.OutOfRange, "Refund amount must be greater than zero.", "amount");
        }

        var wallet = await LockTransactionWallet(session, request.OriginalTransactionId, cancellationToken);
        if (wallet.IsFailure)
        {
            return wallet.Error;
        }

        var existing = await IdempotentResult(session, key.Value, LedgerOperationType.Refund, total.Value, wallet.Value, cancellationToken);
        if (existing is not null)
        {
            return existing.Value;
        }

        var original = await FindPostedPayment(session, request.OriginalTransactionId, cancellationToken);
        if (original.IsFailure)
        {
            return original.Error;
        }

        if (await _store.IsCompensatedAsync(session, original.Value.Id, cancellationToken))
        {
            return RansysError.Financial(ErrorCodes.AlreadyReversed, "A reversed payment cannot be refunded.");
        }

        // Hard safety ceiling (Ledger Posting Rule Matrix §22): total refunds never exceed what the merchant
        // was charged. The stricter refundable-amount policy belongs to the refund use case (ADR-010).
        var availableCode = LedgerAccounts.MerchantAvailable(wallet.Value).Code;
        var refunded = await _store.SumCreditsAsync(
            session, PostingKey.RefundPrefix(request.OriginalTransactionId), availableCode, cancellationToken);
        if (refunded + total.Value.Amount > original.Value.Total.Amount)
        {
            return RansysError.Financial(
                ErrorCodes.RefundExceedsPosted,
                $"Refunds would total {refunded + total.Value.Amount}, more than the posted {original.Value.Total.ToCanonicalAmountString()}.");
        }

        var credited = wallet.Value.CreditAvailable(total.Value);
        if (credited.IsFailure)
        {
            return credited.Error;
        }

        var lines = new List<JournalLine>();
        AddIfPositive(lines, LedgerAccounts.ProviderReceivable(request.ProviderId, wallet.Value.Currency), EntrySide.Debit, request.Principal);
        AddIfPositive(lines, LedgerAccounts.FeeRevenue(wallet.Value.Currency), EntrySide.Debit, request.Fee);
        lines.Add(new JournalLine(LedgerAccounts.MerchantAvailable(wallet.Value), EntrySide.Credit, total.Value));

        var now = _clock.UtcNow;
        var journal = Journal.Create(new JournalDraft(
            _ids.NewId(), key.Value, LedgerOperationType.Refund, request.RefundTransactionId ?? request.OriginalTransactionId,
            null, request.ApprovalRequestId, request.RefundReference, "Refund of posted payment", now, now, request.Actor, null, lines));
        if (journal.IsFailure)
        {
            return journal.Error;
        }

        return await Persist(session, journal.Value, wallet.Value, LedgerEventTypes.RefundPosted, cancellationToken);
    }

    public Task<Result<LedgerPostingResult>> CreditAdjustmentAsync(
        IDatabaseSession session, AdjustmentRequest request, CancellationToken cancellationToken = default) =>
        AdjustAsync(session, request, credit: true, cancellationToken);

    public Task<Result<LedgerPostingResult>> DebitAdjustmentAsync(
        IDatabaseSession session, AdjustmentRequest request, CancellationToken cancellationToken = default) =>
        AdjustAsync(session, request, credit: false, cancellationToken);

    public async Task<Result> ChangeHoldReasonAsync(
        IDatabaseSession session, TransactionId transactionId, string holdReason, CancellationToken cancellationToken = default)
    {
        var reservation = await _store.LockReservationAsync(session, transactionId, cancellationToken);
        if (reservation.IsFailure)
        {
            return reservation.Error;
        }

        if (reservation.Value is null)
        {
            return ReservationNotFound(transactionId);
        }

        var changed = reservation.Value.ChangeHoldReason(holdReason);
        if (changed.IsFailure)
        {
            return changed;
        }

        await _store.UpdateReservationAsync(session, reservation.Value, cancellationToken);
        return Result.Success();
    }

    private async Task<Result<LedgerPostingResult>> AdjustAsync(
        IDatabaseSession session, AdjustmentRequest request, bool credit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ApprovalRequestId == Guid.Empty)
        {
            return ApprovalRequired("Manual adjustment");
        }

        if (string.IsNullOrWhiteSpace(request.Reason) || string.IsNullOrWhiteSpace(request.SupportingReference))
        {
            return RansysError.Validation(
                ErrorCodes.Required, "Manual adjustment requires a reason and a supporting reference.", "reason");
        }

        var key = PostingKey.Adjustment(request.AdjustmentReference);
        if (key.IsFailure)
        {
            return key.Error;
        }

        if (request.Amount.IsZero)
        {
            return RansysError.Validation(ErrorCodes.OutOfRange, "Adjustment amount must be greater than zero.", "amount");
        }

        var operation = credit ? LedgerOperationType.AdjustmentCredit : LedgerOperationType.AdjustmentDebit;
        var wallet = await LockWallet(session, request.WalletId, cancellationToken);
        if (wallet.IsFailure)
        {
            return wallet.Error;
        }

        var existing = await IdempotentResult(session, key.Value, operation, request.Amount, wallet.Value, cancellationToken);
        if (existing is not null)
        {
            return existing.Value;
        }

        var applied = credit ? wallet.Value.CreditAvailable(request.Amount) : wallet.Value.DebitAvailable(request.Amount);
        if (applied.IsFailure)
        {
            return applied.Error;
        }

        var clearing = new JournalLine(
            LedgerAccounts.AdjustmentClearing(wallet.Value.Currency), credit ? EntrySide.Debit : EntrySide.Credit, request.Amount);
        var merchant = new JournalLine(
            LedgerAccounts.MerchantAvailable(wallet.Value), credit ? EntrySide.Credit : EntrySide.Debit, request.Amount);
        var description = $"Manual {(credit ? "credit" : "debit")} adjustment: {request.Reason} (evidence {request.SupportingReference})";

        var now = _clock.UtcNow;
        var journal = Journal.Create(new JournalDraft(
            _ids.NewId(), key.Value, operation, null, null, request.ApprovalRequestId, request.AdjustmentReference,
            description.Length > Journal.MaxDescriptionLength ? description[..Journal.MaxDescriptionLength] : description,
            now, now, request.Actor, null, credit ? [clearing, merchant] : [merchant, clearing]));
        if (journal.IsFailure)
        {
            return journal.Error;
        }

        return await Persist(session, journal.Value, wallet.Value, LedgerEventTypes.AdjustmentPosted, cancellationToken);
    }

    private async Task<Result<Wallet>> LockWallet(IDatabaseSession session, WalletId walletId, CancellationToken cancellationToken)
    {
        var wallet = await _store.LockWalletAsync(session, walletId, cancellationToken);
        if (wallet.IsFailure)
        {
            return wallet.Error;
        }

        return wallet.Value ?? (Result<Wallet>)RansysError.Financial(ErrorCodes.WalletNotFound, $"Wallet {walletId} does not exist.");
    }

    /// <summary>Wallet of a transaction, found through its reservation.</summary>
    private async Task<Result<Wallet>> LockTransactionWallet(
        IDatabaseSession session, TransactionId transactionId, CancellationToken cancellationToken)
    {
        var walletId = await _store.FindReservationWalletAsync(session, transactionId, cancellationToken);
        return walletId is { } id
            ? await LockWallet(session, id, cancellationToken)
            : ReservationNotFound(transactionId);
    }

    /// <summary>Lock order steps 2 and 3: wallet, then reservation.</summary>
    private async Task<Result<(Wallet Wallet, Reservation Reservation)>> LockWalletAndReservation(
        IDatabaseSession session, TransactionId transactionId, CancellationToken cancellationToken)
    {
        var wallet = await LockTransactionWallet(session, transactionId, cancellationToken);
        if (wallet.IsFailure)
        {
            return wallet.Error;
        }

        var reservation = await _store.LockReservationAsync(session, transactionId, cancellationToken);
        if (reservation.IsFailure)
        {
            return reservation.Error;
        }

        return reservation.Value is { } r ? (wallet.Value, r) : ReservationNotFound(transactionId);
    }

    private async Task<Result<PostedJournalSummary>> FindPostedPayment(
        IDatabaseSession session, TransactionId transactionId, CancellationToken cancellationToken)
    {
        var original = await _store.FindJournalAsync(session, PostingKey.Post(transactionId), cancellationToken);
        if (original.IsFailure)
        {
            return original.Error;
        }

        return original.Value ?? (Result<PostedJournalSummary>)RansysError.Financial(
            ErrorCodes.OriginalPostingNotFound, $"Transaction {transactionId} has no posted payment.");
    }

    /// <summary>
    /// Step 4 of the lock order. Returns a result when the key already exists: <see cref="LedgerPostingOutcome.AlreadyPosted"/>
    /// if the terms match, or POSTING_KEY_CONFLICT if the same key was used for a different posting.
    /// </summary>
    private async Task<Result<LedgerPostingResult>?> IdempotentResult(
        IDatabaseSession session, PostingKey key, LedgerOperationType operation, Money total, Wallet wallet, CancellationToken cancellationToken)
    {
        var existing = await _store.FindJournalAsync(session, key, cancellationToken);
        if (existing.IsFailure)
        {
            return existing.Error;
        }

        if (existing.Value is not { } journal)
        {
            return null;
        }

        if (journal.OperationType != operation || journal.Total != total)
        {
            return RansysError.Conflict(
                ErrorCodes.PostingKeyConflict,
                $"Posting key {key} already exists for {CanonicalCodes.LedgerOperationType.ToCode(journal.OperationType)} {journal.Total}.");
        }

        return new LedgerPostingResult(LedgerPostingOutcome.AlreadyPosted, key, journal.Id, WalletBalances.Of(wallet));
    }

    private async Task<Result<LedgerPostingResult>> Persist(
        IDatabaseSession session, Journal journal, Wallet wallet, string eventType, CancellationToken cancellationToken)
    {
        var inserted = await _store.InsertJournalAsync(session, journal, cancellationToken);
        if (inserted.IsFailure)
        {
            return inserted.Error;
        }

        await _store.UpdateWalletAsync(session, wallet, cancellationToken);

        var payload = new LedgerPostingEventV1(
            journal.PostingKey.Value,
            journal.Id,
            CanonicalCodes.LedgerOperationType.ToCode(journal.OperationType),
            journal.TransactionId?.Value,
            wallet.Id.Value,
            journal.Total.Amount,
            journal.Currency.Code,
            journal.Currency.Version,
            wallet.AvailableBalance.Amount,
            wallet.ReservedBalance.Amount,
            wallet.LedgerBalance.Amount,
            journal.CreatedAt);
        await _outbox.EnqueueAsync(
            session,
            new OutboxMessage(
                _ids.NewId(), LedgerEventTypes.AggregateType, wallet.Id.Value, eventType, LedgerPostingEventV1.Version,
                SourceVersion: wallet.VersionNo + 1, payload, journal.CreatedAt),
            cancellationToken);

        return new LedgerPostingResult(LedgerPostingOutcome.Posted, journal.PostingKey, journal.Id, WalletBalances.Of(wallet));
    }

    private static void AddIfPositive(List<JournalLine> lines, LedgerAccountSpec account, EntrySide side, Money amount)
    {
        if (!amount.IsZero)
        {
            lines.Add(new JournalLine(account, side, amount));
        }
    }

    private static RansysError ReservationNotFound(TransactionId transactionId) =>
        RansysError.Financial(ErrorCodes.ReservationNotFound, $"Transaction {transactionId} has no reservation.");

    private static RansysError ApprovalRequired(string operation) =>
        new(ErrorCodes.ApprovalRequired, ErrorCategory.Authorization,
            $"{operation} requires an approved maker-checker request (Architecture Spec §31).");
}
