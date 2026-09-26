using Ransys.Domain.Common;
using Ransys.Domain.Monetary;

namespace Ransys.Domain.Ledger;

/// <summary>
/// Balance hold for one transaction (DDL v1.1 <c>ledger.balance_reservations</c>, Ledger Posting Rule Matrix §33).
/// ACTIVE → COMMITTED or ACTIVE → RELEASED only; corrections after that are new financial actions.
/// IN_DOUBT never releases a hold automatically; only the hold reason changes.
/// </summary>
public sealed class Reservation
{
    /// <summary>DDL v1.1 <c>hold_reason varchar(64)</c>.</summary>
    public const int MaxHoldReasonLength = 64;

    private Reservation(
        Guid id,
        WalletId walletId,
        TransactionId transactionId,
        Money principal,
        Money fee,
        Money total,
        ReservationStatus status,
        string holdReason,
        DateTimeOffset createdAt,
        DateTimeOffset? committedAt,
        DateTimeOffset? releasedAt)
    {
        Id = id;
        WalletId = walletId;
        TransactionId = transactionId;
        Principal = principal;
        Fee = fee;
        Total = total;
        Status = status;
        HoldReason = holdReason;
        CreatedAt = createdAt;
        CommittedAt = committedAt;
        ReleasedAt = releasedAt;
    }

    public Guid Id { get; }

    public WalletId WalletId { get; }

    public TransactionId TransactionId { get; }

    public Money Principal { get; }

    public Money Fee { get; }

    public Money Total { get; }

    public ReservationStatus Status { get; private set; }

    public string HoldReason { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? CommittedAt { get; private set; }

    public DateTimeOffset? ReleasedAt { get; private set; }

    public static Result<Reservation> Open(
        Guid id, WalletId walletId, TransactionId transactionId, Money principal, Money fee, string? holdReason, DateTimeOffset createdAt) =>
        Rehydrate(id, walletId, transactionId, principal, fee, ReservationStatus.Active, holdReason, createdAt, null, null);

    public static Result<Reservation> Rehydrate(
        Guid id,
        WalletId walletId,
        TransactionId transactionId,
        Money principal,
        Money fee,
        ReservationStatus status,
        string? holdReason,
        DateTimeOffset createdAt,
        DateTimeOffset? committedAt,
        DateTimeOffset? releasedAt)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(fee);

        var error = Text.Required(holdReason, "holdReason", MaxHoldReasonLength);
        if (error is not null)
        {
            return error;
        }

        var total = principal.Add(fee);
        if (total.IsFailure)
        {
            return total.Error;
        }

        if (total.Value.IsZero)
        {
            return RansysError.Validation(ErrorCodes.ReserveAmountNotPositive, "Reservation total must be greater than zero.", "amount");
        }

        return new Reservation(
            id, walletId, transactionId, principal, fee, total.Value, status, holdReason!, createdAt, committedAt, releasedAt);
    }

    public Result Commit(DateTimeOffset at)
    {
        if (Status != ReservationStatus.Active)
        {
            return NotActive();
        }

        Status = ReservationStatus.Committed;
        CommittedAt = at;
        return Result.Success();
    }

    public Result Release(DateTimeOffset at)
    {
        if (Status != ReservationStatus.Active)
        {
            return NotActive();
        }

        Status = ReservationStatus.Released;
        ReleasedAt = at;
        return Result.Success();
    }

    /// <summary>OP-05: hold stays active; only the reason is updated (e.g. IN_DOUBT).</summary>
    public Result ChangeHoldReason(string? holdReason)
    {
        if (Status != ReservationStatus.Active)
        {
            return NotActive();
        }

        var error = Text.Required(holdReason, "holdReason", MaxHoldReasonLength);
        if (error is not null)
        {
            return error;
        }

        HoldReason = holdReason!;
        return Result.Success();
    }

    private RansysError NotActive() =>
        RansysError.Financial(ErrorCodes.ReservationNotActive, $"Reservation for transaction {TransactionId} is {Status}.");
}
