using Ransys.Application;
using Ransys.Contracts.Events;
using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Ledger;

namespace Ransys.Ledger;

public enum WalletStatusCommand
{
    Freeze,
    Unfreeze,
    Close,
}

public sealed record WalletStatusChangeRequest(
    WalletId WalletId,
    WalletStatusCommand Command,
    string Reason,
    LedgerActor Actor,
    Guid? ApprovalRequestId = null);

public sealed record WalletStatusChangeResult(WalletStatus PreviousStatus, WalletStatus NewStatus, bool Changed);

/// <summary>
/// Controlled wallet status changes (ADR-016). The wallet row is locked; closing re-checks, under that lock, that
/// balances are zero and nothing is left to finalize. Every real change emits <c>WALLET_STATUS_CHANGED</c> (actor,
/// reason, previous/new status) in the same transaction for the Backoffice audit trail.
/// TODO / Architecture Decision Required: whether freeze/unfreeze/close require maker-checker approval is not
/// specified; an approval reference is carried when supplied.
/// </summary>
public sealed class WalletStatusService(ILedgerStore store, IOutboxWriter outbox, IClock clock, IIdGenerator ids)
{
    public const int MaxReasonLength = 500;

    public async Task<Result<WalletStatusChangeResult>> ChangeStatusAsync(
        IDatabaseSession session, WalletStatusChangeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > MaxReasonLength)
        {
            return RansysError.Validation(ErrorCodes.Required, $"A reason of at most {MaxReasonLength} characters is required.", "reason");
        }

        var locked = await store.LockWalletAsync(session, request.WalletId, cancellationToken);
        if (locked.IsFailure)
        {
            return locked.Error;
        }

        if (locked.Value is not { } wallet)
        {
            return RansysError.Financial(ErrorCodes.WalletNotFound, $"Wallet {request.WalletId} does not exist.");
        }

        var previous = wallet.Status;
        var applied = request.Command switch
        {
            WalletStatusCommand.Freeze => wallet.Freeze(),
            WalletStatusCommand.Unfreeze => wallet.Unfreeze(),
            WalletStatusCommand.Close => wallet.Close(
                await store.HasUnresolvedFinancialActivityAsync(session, wallet.Id, cancellationToken)),
            _ => RansysError.Validation(ErrorCodes.OutOfRange, "Unknown wallet status command.", "command"),
        };
        if (applied.IsFailure)
        {
            return applied.Error;
        }

        if (wallet.Status == previous)
        {
            return new WalletStatusChangeResult(previous, previous, Changed: false);
        }

        await store.UpdateWalletAsync(session, wallet, cancellationToken);

        var now = clock.UtcNow;
        var payload = new WalletStatusChangedV1(
            wallet.Id.Value,
            wallet.MerchantId.Value,
            CanonicalCodes.WalletStatus.ToCode(previous),
            CanonicalCodes.WalletStatus.ToCode(wallet.Status),
            request.Reason,
            request.Actor.Type,
            request.Actor.Id,
            request.ApprovalRequestId,
            now);
        await outbox.EnqueueAsync(
            session,
            new OutboxMessage(
                ids.NewId(), LedgerEventTypes.AggregateType, wallet.Id.Value, LedgerEventTypes.WalletStatusChanged,
                WalletStatusChangedV1.Version, wallet.VersionNo + 1, payload, now),
            cancellationToken);

        return new WalletStatusChangeResult(previous, wallet.Status, Changed: true);
    }
}
