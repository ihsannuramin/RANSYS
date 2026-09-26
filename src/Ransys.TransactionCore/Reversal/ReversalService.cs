using Ransys.Application;
using Ransys.Contracts.Events;
using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Routing;
using Ransys.Domain.Transactions;
using Ransys.Routing;
using Ransys.TransactionCore.Idempotency;

namespace Ransys.TransactionCore.Reversal;

public sealed record StartReversalCommand(
    TransactionId OriginalTransactionId,
    string ClientReference,
    string? IdempotencyKey,
    string ReasonCode,
    ChangeSource Source);

public sealed record ReversalStarted(TransactionId ReversalTransactionId, IdempotencyOutcome Outcome);

/// <summary>
/// Starts a reversal as a child transaction (ADR-012): its own <c>ransys_transaction_id</c>, <c>original_transaction_id</c>,
/// idempotency, attempts and history. The original transaction's state is not touched; it changes only when the child is
/// confirmed (<see cref="Finalization.TransactionFinalizationService"/>). The child is routed to the provider that
/// processed the original, which must support reversal (Architecture Spec §17).
/// </summary>
public sealed class ReversalService(
    ITransactionRepository transactions,
    IdempotencyService idempotency,
    IRoutingStore routing,
    IOutboxWriter outbox,
    IClock clock,
    IIdGenerator ids)
{
    /// <summary>
    /// Runs inside the caller's session (the caller commits). The original row is locked first (parent → child lock order).
    /// An idempotent retry with the same client reference returns the existing child.
    /// </summary>
    public async Task<Result<ReversalStarted>> StartAsync(
        IDatabaseSession session, StartReversalCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var loaded = await transactions.GetAsync(session, command.OriginalTransactionId, forUpdate: true, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        if (loaded.Value is not { Routing: { } originalRouting } original)
        {
            return RansysError.Conflict(
                ErrorCodes.ReversalNotAllowed, $"Transaction {command.OriginalTransactionId} does not exist or was never routed to a provider.");
        }

        var now = clock.UtcNow;
        var child = BuildChild(original, originalRouting, command, now);
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
                var authorized = original.AuthorizeReversal();
                if (authorized.IsFailure)
                {
                    return authorized;
                }

                if (!await routing.ProviderHasCapabilityAsync(session, originalRouting.CurrentProvider.ProviderId, ProviderCapabilities.Reversal, ct))
                {
                    return new RansysError(
                        ErrorCodes.ReversalNotSupported, ErrorCategory.Routing,
                        $"Provider {originalRouting.CurrentProvider.ProviderCode} does not support reversal.");
                }

                var children = await transactions.FindChildrenAsync(session, original.Id, TransactionType.Reversal, ct);
                if (children.Any(c => c.Status != ProcessingStatus.Failed))
                {
                    return RansysError.Conflict(
                        ErrorCodes.ReversalAlreadyActive, $"Transaction {original.Id} already has a reversal in progress or completed.");
                }

                var inserted = await transactions.InsertAsync(session, child.Value, ct);
                if (inserted.IsFailure)
                {
                    return inserted;
                }

                await EnqueueRequestedAsync(session, child.Value, original, command.Source, now, ct);
                return Result.Success();
            },
            cancellationToken);

        return decision.IsSuccess
            ? new ReversalStarted(decision.Value.TransactionId, decision.Value.Outcome)
            : decision.Error;
    }

    private Result<Transaction> BuildChild(
        Transaction original, RoutingDecision originalRouting, StartReversalCommand command, DateTimeOffset now)
    {
        var fingerprint = TransactionFingerprint.Compute(new FingerprintInput(
            original.MerchantId, original.ChannelId, TransactionType.Reversal, original.ProductId,
            null, null, original.Amount, command.ClientReference));
        var identity = TransactionIdentity.Create(
            new TransactionId(ids.NewId()), command.ClientReference, command.IdempotencyKey, fingerprint, original.Id);
        if (identity.IsFailure)
        {
            return identity.Error;
        }

        var references = TransactionReferences.Create(command.ClientReference);
        if (references.IsFailure)
        {
            return references.Error;
        }

        var created = Transaction.Create(new TransactionDraft(
            identity.Value, TransactionType.Reversal, original.MerchantId, original.ChannelId, original.ProductId,
            original.Amount, null, null, null, references.Value, ExtensionMetadata.Empty, now));
        if (created.IsFailure)
        {
            return created.Error;
        }

        var child = created.Value;
        var context = TransitionContext.Create(command.ReasonCode, command.Source, now);
        if (context.IsFailure)
        {
            return context.Error;
        }

        var validated = child.Validate(null, original.Configuration, context.Value);
        if (validated.IsFailure)
        {
            return validated.Error;
        }

        // The reversal must reach the provider that may have processed the original.
        var routing = RoutingDecision.Initial(originalRouting.CurrentProvider, originalRouting.RuleVersion, now);
        var processing = child.BeginProcessing(routing.Value, context.Value);
        return processing.IsSuccess ? child : processing.Error;
    }

    private Task EnqueueRequestedAsync(
        IDatabaseSession session, Transaction child, Transaction original, ChangeSource source, DateTimeOffset now, CancellationToken cancellationToken) =>
        outbox.EnqueueAsync(
            session,
            new OutboxMessage(
                ids.NewId(), TransactionEventTypes.AggregateType, original.Id.Value, TransactionEventTypes.ReversalRequested,
                TransactionStatusChangedV1.Version, original.RowVersion,
                new TransactionStatusChangedV1(
                    original.Id.Value,
                    original.Identity.ClientReference,
                    CanonicalCodes.ProcessingStatus.ToCode(original.ProcessingStatus),
                    CanonicalCodes.FinancialStatus.ToCode(original.FinancialStatus),
                    CanonicalCodes.ReconciliationStatus.ToCode(original.ReconciliationStatus),
                    CanonicalCodes.SettlementStatus.ToCode(original.SettlementStatus),
                    original.ResponseCode,
                    $"REVERSAL_REQUESTED:{child.Id}",
                    CanonicalCodes.ChangeSource.ToCode(source),
                    now),
                now),
            cancellationToken);
}
