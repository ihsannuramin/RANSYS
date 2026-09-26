using Ransys.Application;
using Ransys.Contracts.Events;
using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Monetary;
using Ransys.Domain.Routing;
using Ransys.Domain.Transactions;
using Ransys.Routing;
using Ransys.TransactionCore.Idempotency;

namespace Ransys.TransactionCore.Children;

/// <summary>Start of a REFUND child (ADR-023). <paramref name="Amount"/> must use the original's currency definition.</summary>
public sealed record StartRefundCommand(
    TransactionId OriginalTransactionId,
    string ClientReference,
    string? IdempotencyKey,
    Money Amount,
    string ReasonCode,
    string? ReasonDescription,
    ExtensionMetadata Metadata,
    ChangeSource Source);

/// <summary>Start of a VOID child (ADR-019).</summary>
public sealed record StartVoidCommand(
    TransactionId OriginalTransactionId,
    string ClientReference,
    string? IdempotencyKey,
    string ReasonCode,
    string? ReasonDescription,
    ExtensionMetadata Metadata,
    ChangeSource Source);

public sealed record ChildStarted(TransactionId ChildTransactionId, IdempotencyOutcome Outcome);

/// <summary>
/// Starts REFUND and VOID child transactions, following the ADR-012 reversal pattern (<see cref="Reversal.ReversalService"/>):
/// own id, <c>original_transaction_id</c>, idempotency, attempts and history; the original is locked first (parent → child)
/// and never changed while the child runs. The child is routed to the provider that processed the original, which must
/// have the REFUND / VOID capability (VOID is never treated as reversal or refund, ADR-017).
/// <list type="bullet">
/// <item>Refund: <see cref="Transaction.AuthorizeRefund"/>; same currency definition; the child amount plus every refund
/// child that has not FAILED (in flight or succeeded) never exceeds the original principal. The ledger's
/// REFUND_EXCEEDS_POSTED check stays the final backstop.</item>
/// <item>Void: <see cref="Transaction.AuthorizeVoid"/>; at most one VOID per original that has not FAILED (migration 0008).</item>
/// </list>
/// </summary>
public sealed class ChildTransactionService(
    ITransactionRepository transactions,
    IdempotencyService idempotency,
    IRoutingStore routing,
    IOutboxWriter outbox,
    IClock clock,
    IIdGenerator ids)
{
    public const string VoidAlreadyActive = "VOID_ALREADY_ACTIVE";

    /// <summary>Runs inside the caller's session (the caller commits).</summary>
    public async Task<Result<ChildStarted>> StartRefundAsync(
        IDatabaseSession session, StartRefundCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return await StartAsync(
            session, TransactionType.Refund, command.OriginalTransactionId, command.ClientReference, command.IdempotencyKey,
            _ => command.Amount, command.ReasonCode, command.ReasonDescription, command.Metadata, command.Source,
            async (original, child, ct) =>
            {
                var authorized = original.AuthorizeRefund();
                if (authorized.IsFailure)
                {
                    return authorized;
                }

                if (child.Amount.Currency != original.Amount.Currency)
                {
                    return RansysError.Validation(ErrorCodes.CurrencyMismatch, "A refund must use the original transaction's currency.", "refundAmount");
                }

                if (child.Amount.IsZero)
                {
                    return RansysError.Validation(ErrorCodes.OutOfRange, "Refund amount must be greater than zero.", "refundAmount");
                }

                var siblings = await transactions.FindChildSummariesAsync(session, original.Id, TransactionType.Refund, ct);
                var committed = siblings.Where(c => c.Status != ProcessingStatus.Failed).Sum(c => c.Amount);
                if (committed + child.Amount.Amount > original.Amount.Amount)
                {
                    return RansysError.Financial(
                        ErrorCodes.RefundExceedsPosted,
                        $"Refunds of {original.Id} would total {committed + child.Amount.Amount}, more than its principal {original.Amount.ToCanonicalAmountString()}.");
                }

                return Result.Success();
            },
            ProviderCapabilities.Refund,
            TransactionEventTypes.RefundRequested,
            cancellationToken);
    }

    /// <summary>Runs inside the caller's session (the caller commits).</summary>
    public async Task<Result<ChildStarted>> StartVoidAsync(
        IDatabaseSession session, StartVoidCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return await StartAsync(
            session, TransactionType.Void, command.OriginalTransactionId, command.ClientReference, command.IdempotencyKey,
            original => original.Amount, command.ReasonCode, command.ReasonDescription, command.Metadata, command.Source,
            async (original, _, ct) =>
            {
                var authorized = original.AuthorizeVoid();
                if (authorized.IsFailure)
                {
                    return authorized;
                }

                var children = await transactions.FindChildrenAsync(session, original.Id, TransactionType.Void, ct);
                return children.Any(c => c.Status != ProcessingStatus.Failed)
                    ? RansysError.Conflict(VoidAlreadyActive, $"Transaction {original.Id} already has a VOID in progress or completed.")
                    : Result.Success();
            },
            ProviderCapabilities.Void,
            TransactionEventTypes.VoidRequested,
            cancellationToken);
    }

    private async Task<Result<ChildStarted>> StartAsync(
        IDatabaseSession session,
        TransactionType type,
        TransactionId originalId,
        string clientReference,
        string? idempotencyKey,
        Func<Transaction, Money> amountOf,
        string reasonCode,
        string? reasonDescription,
        ExtensionMetadata metadata,
        ChangeSource source,
        Func<Transaction, Transaction, CancellationToken, Task<Result>> authorize,
        string capability,
        string requestedEventType,
        CancellationToken cancellationToken)
    {
        var loaded = await transactions.GetAsync(session, originalId, forUpdate: true, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        if (loaded.Value is not { Routing: { } originalRouting } original)
        {
            return RansysError.Conflict(
                type == TransactionType.Refund ? ErrorCodes.RefundNotAllowed : ErrorCodes.VoidNotAllowed,
                $"Transaction {originalId} does not exist or was never routed to a provider.");
        }

        var now = clock.UtcNow;
        var child = BuildChild(type, original, originalRouting, clientReference, idempotencyKey, amountOf(original), reasonCode, reasonDescription, metadata, source, now);
        if (child.IsFailure)
        {
            return child.Error;
        }

        var decision = await idempotency.ClaimAsync(
            session,
            original.ChannelId,
            child.Value.Identity,
            async ct =>
            {
                var authorized = await authorize(original, child.Value, ct);
                if (authorized.IsFailure)
                {
                    return authorized;
                }

                if (!await routing.ProviderHasCapabilityAsync(session, originalRouting.CurrentProvider.ProviderId, capability, ct))
                {
                    return new RansysError(
                        ErrorCodes.NoRouteAvailable, ErrorCategory.Routing,
                        $"Provider {originalRouting.CurrentProvider.ProviderCode} does not support {capability}.");
                }

                var inserted = await transactions.InsertAsync(session, child.Value, ct);
                if (inserted.IsFailure)
                {
                    return inserted;
                }

                await EnqueueRequestedAsync(session, requestedEventType, child.Value, original, source, now, ct);
                return Result.Success();
            },
            cancellationToken);

        return decision.IsSuccess
            ? new ChildStarted(decision.Value.TransactionId, decision.Value.Outcome)
            : decision.Error;
    }

    private Result<Transaction> BuildChild(
        TransactionType type,
        Transaction original,
        RoutingDecision originalRouting,
        string clientReference,
        string? idempotencyKey,
        Money amount,
        string reasonCode,
        string? reasonDescription,
        ExtensionMetadata metadata,
        ChangeSource source,
        DateTimeOffset now)
    {
        var fingerprint = TransactionFingerprint.Compute(new FingerprintInput(
            original.MerchantId, original.ChannelId, type, original.ProductId, null, null, amount, clientReference));
        var identity = TransactionIdentity.Create(new TransactionId(ids.NewId()), clientReference, idempotencyKey, fingerprint, original.Id);
        if (identity.IsFailure)
        {
            return identity.Error;
        }

        var references = TransactionReferences.Create(clientReference);
        if (references.IsFailure)
        {
            return references.Error;
        }

        var created = Transaction.Create(new TransactionDraft(
            identity.Value, type, original.MerchantId, original.ChannelId, original.ProductId,
            amount, null, null, null, references.Value, metadata, now));
        if (created.IsFailure)
        {
            return created.Error;
        }

        var child = created.Value;
        var context = TransitionContext.Create(reasonCode, source, now, reasonDescription);
        if (context.IsFailure)
        {
            return context.Error;
        }

        var validated = child.Validate(null, original.Configuration, context.Value);
        if (validated.IsFailure)
        {
            return validated.Error;
        }

        // The child must reach the provider that processed the original.
        var routingDecision = RoutingDecision.Initial(originalRouting.CurrentProvider, originalRouting.RuleVersion, now);
        var processing = child.BeginProcessing(routingDecision.Value, context.Value);
        return processing.IsSuccess ? child : processing.Error;
    }

    private Task EnqueueRequestedAsync(
        IDatabaseSession session, string eventType, Transaction child, Transaction original, ChangeSource source, DateTimeOffset now, CancellationToken cancellationToken) =>
        outbox.EnqueueAsync(
            session,
            new OutboxMessage(
                ids.NewId(), TransactionEventTypes.AggregateType, original.Id.Value, eventType,
                TransactionStatusChangedV1.Version, original.RowVersion,
                new TransactionStatusChangedV1(
                    original.Id.Value,
                    original.Identity.ClientReference,
                    CanonicalCodes.ProcessingStatus.ToCode(original.ProcessingStatus),
                    CanonicalCodes.FinancialStatus.ToCode(original.FinancialStatus),
                    CanonicalCodes.ReconciliationStatus.ToCode(original.ReconciliationStatus),
                    CanonicalCodes.SettlementStatus.ToCode(original.SettlementStatus),
                    original.ResponseCode,
                    $"{eventType}:{child.Id}",
                    CanonicalCodes.ChangeSource.ToCode(source),
                    now),
                now),
            cancellationToken);
}
