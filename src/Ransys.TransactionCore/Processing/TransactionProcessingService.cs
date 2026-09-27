using System.Collections.Immutable;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Ransys.Application;
using Ransys.Domain;
using Ransys.Domain.Attempts;
using Ransys.Domain.Common;
using Ransys.Domain.Monetary;
using Ransys.Domain.Routing;
using Ransys.Domain.Transactions;
using Ransys.Ledger;
using Ransys.Routing;
using Ransys.TransactionCore.Attempts;
using Ransys.TransactionCore.Children;
using Ransys.TransactionCore.Fees;
using Ransys.TransactionCore.Finalization;
using Ransys.TransactionCore.Idempotency;
using Ransys.TransactionCore.Providers;
using Ransys.TransactionCore.Reversal;

namespace Ransys.TransactionCore.Processing;

/// <summary>
/// Orchestrates one merchant request end to end (Architecture Spec §12, Sequence Pack SD-01/SD-05; M12d):
/// <list type="number">
/// <item><b>Session 1 — durable before any provider call.</b> Resolve reference data, build the transaction and claim the
/// client reference (<see cref="IdempotencyService.ClaimAsync"/>). Inside the claim: route, resolve fees (ADR-020),
/// validate, insert, reserve through the ledger (reserving types), begin processing and start the attempt. Business
/// rejections that happen here (insufficient balance, frozen wallet, no route) end the transaction FAILED — committed,
/// no provider call. An idempotent retry returns the existing transaction as it is now. A database failure throws
/// <see cref="FinancialDependencyUnavailableException"/>; the provider is never called.</item>
/// <item><b>Provider call</b> outside any database transaction (<see cref="ProviderInvoker"/>, bounded by the call policy).</item>
/// <item><b>Session 2.</b> Record the attempt outcome once, then apply the result through
/// <see cref="TransactionFinalizationService"/> (<see cref="ChangeSource.SyncProviderResponse"/>). A proven NOT_SENT fails
/// over to the next eligible provider (bounded by <see cref="TransactionProcessingOptions.MaxProviderAttempts"/>) with a new
/// attempt committed before the next call; with no provider left the transaction fails and the reservation is released
/// (5001). Child transactions never fail over: they must reach the original's provider.</item>
/// </list>
/// After session 1 commits, the request is carried to a recorded result even if the caller goes away
/// (<see cref="CancellationToken.None"/>). If session 2 cannot be written, the result is reported as IN_DOUBT (1002): the
/// attempt stays outcome-less, which <see cref="AttemptRecoveryService"/> turns into IN_DOUBT with the hold kept (ADR-005).
/// </summary>
public sealed class TransactionProcessingService(
    IDatabaseSessionFactory sessions,
    ITransactionRepository transactions,
    ITransactionAttemptStore attemptStore,
    TransactionAttemptService attempts,
    IdempotencyService idempotency,
    ILedgerPostingService ledger,
    RoutingService routing,
    FeeResolver fees,
    IReferenceDataReader referenceData,
    ReversalService reversals,
    ChildTransactionService children,
    TransactionFinalizationService finalization,
    ProviderRequestFactory requestFactory,
    ProviderInvoker invoker,
    ITransactionQuery query,
    IClock clock,
    TransactionProcessingOptions options)
{
    public const string ReasonValidated = "VALIDATION_OK";
    public const string ReasonReserved = "BALANCE_RESERVED";
    public const string ReasonProcessing = "PROVIDER_PROCESSING";
    public const string ReasonFinalizationFailed = "FINALIZATION_FAILED";

    private static readonly IReadOnlyDictionary<string, JsonElement> NoData = ImmutableDictionary<string, JsonElement>.Empty;

    public Task<Result<TransactionProcessingResult>> InquireAsync(InquiryCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ProcessOriginalAsync(
            new OriginalRequest(
                command.ChannelId, command.MerchantId, TransactionType.Inquiry, command.ClientReference, command.IdempotencyKey,
                command.ProductCode, null, command.Customer, null, command.Destination, null, command.Metadata),
            cancellationToken);
    }

    public Task<Result<TransactionProcessingResult>> PayAsync(PaymentCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ProcessOriginalAsync(
            new OriginalRequest(
                command.ChannelId, command.MerchantId, TransactionType.Payment, command.ClientReference, command.IdempotencyKey,
                command.ProductCode, command.Amount, command.Customer, command.Source, command.Destination, command.MerchantReference,
                command.Metadata),
            cancellationToken);
    }

    public Task<Result<TransactionProcessingResult>> TransferAsync(TransferCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ProcessOriginalAsync(
            new OriginalRequest(
                command.ChannelId, command.MerchantId, TransactionType.Transfer, command.ClientReference, command.IdempotencyKey,
                command.ProductCode, command.Amount, command.Customer, command.Source, command.Destination, null, command.Metadata),
            cancellationToken);
    }

    public Task<Result<TransactionProcessingResult>> RefundAsync(RefundCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ProcessChildAsync(
            new ChildRequest(
                command.ChannelId, command.MerchantId, TransactionType.Refund, command.ClientReference, command.IdempotencyKey,
                command.OriginalTransactionId, command.RefundAmount, command.Reason, command.Metadata),
            cancellationToken);
    }

    public Task<Result<TransactionProcessingResult>> ReverseAsync(ReversalCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ProcessChildAsync(
            new ChildRequest(
                command.ChannelId, command.MerchantId, TransactionType.Reversal, command.ClientReference, command.IdempotencyKey,
                command.OriginalTransactionId, null, command.Reason, command.Metadata),
            cancellationToken);
    }

    public Task<Result<TransactionProcessingResult>> VoidAsync(VoidCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ProcessChildAsync(
            new ChildRequest(
                command.ChannelId, command.MerchantId, TransactionType.Void, command.ClientReference, command.IdempotencyKey,
                command.OriginalTransactionId, null, command.Reason, command.Metadata),
            cancellationToken);
    }

    /// <summary>
    /// <c>GET /api/v1/transactions/{id}</c>: the public view, or null when the transaction does not exist or is not visible to
    /// <paramref name="channelId"/> (the API answers 404 in both cases). Throws <see cref="FinancialDependencyUnavailableException"/>
    /// when the Transaction DB is unavailable.
    /// </summary>
    public async Task<TransactionDetailView?> GetTransactionAsync(
        ChannelId channelId, TransactionId transactionId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var session = await sessions.BeginAsync(cancellationToken);
            var row = await query.FindForChannelAsync(session, channelId, transactionId, cancellationToken);
            if (row is null || row.ChannelId != channelId)
            {
                return null;
            }

            var code = RansysResponseCodes.ForTransaction(row.ProcessingStatus, row.ResponseCode);
            return new TransactionDetailView(
                row.TransactionId, row.OriginalTransactionId, row.ClientReference, row.TransactionType, row.ProcessingStatus,
                row.FinancialStatus, row.ReconciliationStatus, row.SettlementStatus, code,
                RansysResponseCodes.MessageFor(code, row.ProcessingStatus),
                new PublicReferences(row.MerchantReference, row.Stan, row.Rrn), row.ReceivedAt, row.CompletedAt);
        }
        catch (Exception ex) when (IsDependencyFailure(ex))
        {
            throw new FinancialDependencyUnavailableException("The Transaction DB is unavailable.", ex);
        }
    }

    // ------------------------------------------------------------------ originals (inquiry, payment, transfer)

    private async Task<Result<TransactionProcessingResult>> ProcessOriginalAsync(OriginalRequest request, CancellationToken cancellationToken)
    {
        var prepared = await InDurableSessionAsync(session => PrepareOriginalAsync(session, request, cancellationToken), cancellationToken);
        return prepared.IsFailure ? prepared.Error : await CompleteAsync(prepared.Value);
    }

    private async Task<Result<Prepared>> PrepareOriginalAsync(IDatabaseSession session, OriginalRequest request, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var monetary = request.Type != TransactionType.Inquiry;
        if (monetary && request.Amount is null)
        {
            return RansysError.Validation(ErrorCodes.Required, "amount is required.", "amount");
        }

        if (request.Type == TransactionType.Transfer && (request.Source is null || request.Destination is null))
        {
            return RansysError.Validation(ErrorCodes.Required, "A transfer needs source and destination.", request.Source is null ? "source" : "destination");
        }

        if (string.IsNullOrWhiteSpace(request.ProductCode))
        {
            return RansysError.Validation(ErrorCodes.Required, "productCode is required.", "productCode");
        }

        // ADR-025: look for an existing claim before resolving any "currently active" reference data. A legitimate
        // retry of the same payload must replay the original transaction even if the product has since gone
        // INACTIVE or a new currency definition version has since become active — those are not the merchant's
        // payload changing.
        var peeked = await idempotency.PeekActiveAsync(session, request.ChannelId, request.ClientReference, cancellationToken);
        if (peeked.IsFailure)
        {
            return peeked.Error;
        }

        if (peeked.Value is { } existingClaim)
        {
            return await ResolveReplayAsync(session, request, existingClaim, cancellationToken);
        }

        var product = await referenceData.FindActiveProductByCodeAsync(session, request.ProductCode, cancellationToken);
        if (product is null)
        {
            return RansysError.Validation(ProcessingErrorCodes.ProductNotAvailable, $"Product '{request.ProductCode}' is not available.", "productCode");
        }

        var currencyCode = request.Amount?.Currency ?? options.NonMonetaryCurrencyCode;
        var currency = await referenceData.FindActiveCurrencyAsync(session, currencyCode, now, cancellationToken);
        if (currency.IsFailure)
        {
            return currency.Error;
        }

        if (currency.Value is not { } definition)
        {
            return RansysError.Validation(ProcessingErrorCodes.CurrencyNotSupported, $"Currency '{currencyCode}' is not supported.", "amount.currency");
        }

        var amount = Money.Create(request.Amount?.Value ?? 0m, definition);
        if (amount.IsFailure)
        {
            return amount.Error with { Field = "amount.value" };
        }

        if (monetary && amount.Value.IsZero)
        {
            return RansysError.Validation(ErrorCodes.OutOfRange, "amount must be greater than zero.", "amount.value");
        }

        WalletInfo? wallet = null;
        if (TransactionTypeRules.RequiresReservation(request.Type))
        {
            var found = await referenceData.FindMerchantMainWalletAsync(session, request.MerchantId, definition, cancellationToken);
            if (found.IsFailure)
            {
                return found.Error;
            }

            wallet = found.Value;
            if (wallet is null)
            {
                return RansysError.Financial(
                    ErrorCodes.WalletNotFound, $"Merchant {request.MerchantId} has no usable {definition.Code} wallet.");
            }
        }

        var built = BuildOriginal(request, product.ProductId, amount.Value, now);
        if (built.IsFailure)
        {
            return built.Error;
        }

        var transaction = built.Value;
        TransactionAttempt? attempt = null;
        var claim = await idempotency.ClaimAsync(
            session,
            request.ChannelId,
            transaction.Identity,
            async ct =>
            {
                var created = await CreateOriginalAsync(session, transaction, wallet, now, ct);
                if (created.IsFailure)
                {
                    return created.Error;
                }

                attempt = created.Value;
                return Result.Success();
            },
            cancellationToken);
        if (claim.IsFailure)
        {
            return claim.Error;
        }

        if (claim.Value.Outcome == IdempotencyOutcome.ExistingTransaction)
        {
            return await ReplayAsync(session, claim.Value.TransactionId, request.Type, expectedOriginal: null, cancellationToken);
        }

        if (attempt is null)
        {
            // Ended before any provider call (e.g. insufficient balance, no route): return the committed state.
            var current = await LoadAsync(session, transaction.Id, cancellationToken);
            return current.IsFailure ? current.Error : new Prepared(current.Value, null, null, OriginalProviderReferences.None, product.ProductCode, null, IsReplay: false);
        }

        return new Prepared(transaction, attempt, null, OriginalProviderReferences.None, product.ProductCode, null, IsReplay: false);
    }

    /// <summary>
    /// An active claim already exists for this channel + client reference (ADR-025). Compare the request against the
    /// <em>original transaction's own snapshot</em> — never against currently-active reference data, which can have
    /// drifted (product deactivated, a new currency definition version active) since the original request without the
    /// merchant's payload changing. A genuine payload mismatch (different product, different currency, or any other
    /// fingerprinted field) is <see cref="ErrorCodes.DuplicateReferenceConflict"/>; an identical payload replays the
    /// original transaction, with no product/currency/wallet resolution and no new claim.
    /// </summary>
    private async Task<Result<Prepared>> ResolveReplayAsync(
        IDatabaseSession session, OriginalRequest request, ExistingClaim claim, CancellationToken cancellationToken)
    {
        var existing = await LoadAsync(session, claim.TransactionId, cancellationToken);
        if (existing.IsFailure)
        {
            return existing.Error;
        }

        var existingTransaction = existing.Value;
        var productId = await referenceData.FindProductIdByCodeAsync(session, request.ProductCode, cancellationToken);
        var currencyMismatch = request.Amount is { } amountInput
            && !string.Equals(amountInput.Currency, existingTransaction.Amount.CurrencyCode, StringComparison.Ordinal);
        if (productId is null || productId != existingTransaction.ProductId || currencyMismatch)
        {
            return DuplicateReferenceConflict(request.ClientReference);
        }

        var candidateAmount = Money.Create(request.Amount?.Value ?? 0m, existingTransaction.Amount.Currency);
        if (candidateAmount.IsFailure)
        {
            return candidateAmount.Error with { Field = "amount.value" };
        }

        var candidateSource = ToEndpoint(request.Source, "source");
        var candidateDestination = ToEndpoint(request.Destination, "destination");
        var endpointError = candidateSource.IsFailure ? candidateSource.Error : candidateDestination.IsFailure ? candidateDestination.Error : (RansysError?)null;
        if (endpointError is { } error)
        {
            return error;
        }

        var candidateFingerprint = TransactionFingerprint.Compute(new FingerprintInput(
            request.MerchantId, request.ChannelId, request.Type, existingTransaction.ProductId,
            candidateSource.Value, candidateDestination.Value, candidateAmount.Value, request.ClientReference));

        if (candidateFingerprint != claim.Fingerprint)
        {
            return DuplicateReferenceConflict(request.ClientReference);
        }

        return await ReplayAsync(session, claim.TransactionId, request.Type, expectedOriginal: null, cancellationToken);
    }

    private static RansysError DuplicateReferenceConflict(string clientReference) =>
        RansysError.Conflict(
            ErrorCodes.DuplicateReferenceConflict, $"Client reference '{clientReference}' was already used with a different payload.");

    /// <summary>
    /// Runs inside the idempotency claim (savepoint). Returns the started attempt, or null when the transaction was
    /// ended before any provider call (committed as FAILED).
    /// </summary>
    private async Task<Result<TransactionAttempt?>> CreateOriginalAsync(
        IDatabaseSession session, Transaction transaction, WalletInfo? wallet, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Routing is read first so its configuration version is captured with the fee version at validation.
        var route = await routing.RouteAsync(
            session,
            new RoutingRequest(transaction.Id, transaction.Type, transaction.ProductId, transaction.MerchantId, transaction.ChannelId, transaction.Amount, []),
            cancellationToken);

        var fee = await fees.ResolveAsync(
            session, transaction.ProductId, transaction.Type, transaction.MerchantId, transaction.Amount, now, cancellationToken);
        if (fee.IsFailure)
        {
            return fee.Error;
        }

        var snapshot = new TransactionConfigurationSnapshot(
            ConfigVersionId: null,
            RoutingConfigVersionId: route.IsSuccess ? route.Value.ConfigVersionId : null,
            FeeConfigVersionId: fee.Value.FeeConfigVersionId,
            ProviderPolicyVersion: null);
        var validated = transaction.Validate(fee.Value.Fees, snapshot, Context(ReasonValidated, now));
        if (validated.IsFailure)
        {
            return validated.Error;
        }

        var inserted = await transactions.InsertAsync(session, transaction, cancellationToken);
        if (inserted.IsFailure)
        {
            return inserted.Error;
        }

        if (transaction.RequiresReservation)
        {
            var reserved = await ledger.ReserveAsync(
                session,
                new ReserveRequest(transaction.Id, wallet!.WalletId, transaction.Amount, transaction.Fees!.GuaranteedReserveFeeTotal, "PAYMENT_PROCESSING"),
                cancellationToken);
            if (reserved.IsFailure)
            {
                if (reserved.Error.Code is not (ErrorCodes.InsufficientBalance or ErrorCodes.WalletNotActive or ErrorCodes.WalletClosed))
                {
                    return reserved.Error;
                }

                // Expected business outcome: the transaction ends FAILED (nothing reserved), committed, no provider call.
                var failed = await FailBeforeSendAsync(
                    session, transaction.Id, reserved.Error.Code,
                    reserved.Error.Code == ErrorCodes.InsufficientBalance ? RansysResponseCodes.InsufficientBalance : null,
                    reserved.Error.Message, null, cancellationToken);
                return failed.IsFailure ? failed.Error : Result<TransactionAttempt?>.Success(null);
            }

            var marked = transaction.MarkReserved(Context(ReasonReserved, now));
            if (marked.IsFailure)
            {
                return marked.Error;
            }

            var saved = await transactions.UpdateAsync(session, transaction, cancellationToken);
            if (saved.IsFailure)
            {
                return saved.Error;
            }
        }

        if (route.IsFailure)
        {
            if (route.Error.Code is not (ErrorCodes.NoRouteAvailable or ErrorCodes.ConfigurationNotAvailable))
            {
                return route.Error;
            }

            // No eligible provider: FAILED; an active reservation is released (5001).
            var failed = await FailBeforeSendAsync(
                session, transaction.Id, ErrorCodes.NoRouteAvailable, RansysResponseCodes.NoRouteAvailable, route.Error.Message, null, cancellationToken);
            return failed.IsFailure ? failed.Error : Result<TransactionAttempt?>.Success(null);
        }

        var decision = route.Value.ToInitialDecision();
        if (decision.IsFailure)
        {
            return decision.Error;
        }

        var processing = transaction.BeginProcessing(decision.Value, Context(ReasonProcessing, now));
        if (processing.IsFailure)
        {
            return processing.Error;
        }

        var updated = await transactions.UpdateAsync(session, transaction, cancellationToken);
        if (updated.IsFailure)
        {
            return updated.Error;
        }

        var attempt = await StartAttemptAsync(session, transaction, decision.Value.CurrentProvider, cancellationToken);
        return attempt.IsFailure ? attempt.Error : attempt.Value;
    }

    private static Result<Transaction> BuildOriginal(OriginalRequest request, ProductId productId, Money amount, DateTimeOffset now)
    {
        var customer = ToCustomer(request.Customer);
        var source = ToEndpoint(request.Source, "source");
        var destination = ToEndpoint(request.Destination, "destination");
        var metadata = ToMetadata(request.Metadata, "metadata");
        var references = TransactionReferences.Create(request.ClientReference, request.MerchantReference);
        var error = new[]
        {
            customer.IsFailure ? customer.Error : null,
            source.IsFailure ? source.Error : null,
            destination.IsFailure ? destination.Error : null,
            metadata.IsFailure ? metadata.Error : null,
            references.IsFailure ? references.Error : null,
        }.FirstOrDefault(e => e is not null);
        if (error is not null)
        {
            return error;
        }

        var fingerprint = TransactionFingerprint.Compute(new FingerprintInput(
            request.MerchantId, request.ChannelId, request.Type, productId, source.Value, destination.Value, amount, request.ClientReference));
        var identity = TransactionIdentity.Create(
            new TransactionId(Guid.CreateVersion7()), request.ClientReference, request.IdempotencyKey, fingerprint);
        if (identity.IsFailure)
        {
            return identity.Error;
        }

        return Transaction.Create(new TransactionDraft(
            identity.Value, request.Type, request.MerchantId, request.ChannelId, productId, amount,
            customer.Value, source.Value, destination.Value, references.Value, metadata.Value, now));
    }

    // ------------------------------------------------------------------ children (refund, reversal, void)

    private async Task<Result<TransactionProcessingResult>> ProcessChildAsync(ChildRequest request, CancellationToken cancellationToken)
    {
        var prepared = await InDurableSessionAsync(session => PrepareChildAsync(session, request, cancellationToken), cancellationToken);
        return prepared.IsFailure ? prepared.Error : await CompleteAsync(prepared.Value);
    }

    private async Task<Result<Prepared>> PrepareChildAsync(IDatabaseSession session, ChildRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > TransitionContext.MaxReasonDescriptionLength)
        {
            return RansysError.Validation(
                string.IsNullOrWhiteSpace(request.Reason) ? ErrorCodes.Required : ErrorCodes.TooLong,
                $"reason is required and at most {TransitionContext.MaxReasonDescriptionLength} characters.", "reason");
        }

        var metadata = ToMetadata(request.Metadata, "metadata");
        if (metadata.IsFailure)
        {
            return metadata.Error;
        }

        if (request.OriginalTransactionId == Guid.Empty)
        {
            return OriginalInvalid();
        }

        var originalId = new TransactionId(request.OriginalTransactionId);
        var peek = await transactions.GetAsync(session, originalId, forUpdate: false, cancellationToken);
        if (peek.IsFailure)
        {
            return peek.Error;
        }

        // ADR-021: unknown and not-owned originals are indistinguishable to the caller.
        if (peek.Value is not { } visible || visible.ChannelId != request.ChannelId || visible.MerchantId != request.MerchantId)
        {
            return OriginalInvalid();
        }

        Result<(TransactionId Id, IdempotencyOutcome Outcome)> started;
        switch (request.Type)
        {
            case TransactionType.Reversal:
                var reversal = await reversals.StartAsync(
                    session,
                    new StartReversalCommand(originalId, request.ClientReference, request.IdempotencyKey, "REVERSAL_REQUESTED", ChangeSource.Core, request.Reason, metadata.Value),
                    cancellationToken);
                started = reversal.IsSuccess ? (reversal.Value.ReversalTransactionId, reversal.Value.Outcome) : reversal.Error;
                break;

            case TransactionType.Refund:
                if (request.RefundAmount is not { } refundInput)
                {
                    return RansysError.Validation(ErrorCodes.Required, "refundAmount is required.", "refundAmount");
                }

                if (!string.Equals(refundInput.Currency, visible.Amount.CurrencyCode, StringComparison.Ordinal))
                {
                    return RansysError.Validation(ErrorCodes.CurrencyMismatch, "refundAmount must use the original transaction's currency.", "refundAmount.currency");
                }

                // The refund keeps the original's currency definition (Ledger Posting Rule Matrix §50).
                var refundAmount = Money.Create(refundInput.Value, visible.Amount.Currency);
                if (refundAmount.IsFailure)
                {
                    return refundAmount.Error with { Field = "refundAmount.value" };
                }

                var refund = await children.StartRefundAsync(
                    session,
                    new StartRefundCommand(originalId, request.ClientReference, request.IdempotencyKey, refundAmount.Value, "REFUND_REQUESTED", request.Reason, metadata.Value, ChangeSource.Core),
                    cancellationToken);
                started = refund.IsSuccess ? (refund.Value.ChildTransactionId, refund.Value.Outcome) : refund.Error;
                break;

            default:
                var voided = await children.StartVoidAsync(
                    session,
                    new StartVoidCommand(originalId, request.ClientReference, request.IdempotencyKey, "VOID_REQUESTED", request.Reason, metadata.Value, ChangeSource.Core),
                    cancellationToken);
                started = voided.IsSuccess ? (voided.Value.ChildTransactionId, voided.Value.Outcome) : voided.Error;
                break;
        }

        if (started.IsFailure)
        {
            return started.Error;
        }

        if (started.Value.Outcome == IdempotencyOutcome.ExistingTransaction)
        {
            return await ReplayAsync(session, started.Value.Id, request.Type, originalId, cancellationToken);
        }

        // Parent → child: the start service already holds the original's row lock in this session.
        var original = await transactions.GetAsync(session, originalId, forUpdate: true, cancellationToken);
        var child = await transactions.GetAsync(session, started.Value.Id, forUpdate: true, cancellationToken);
        if (original.IsFailure || child.IsFailure)
        {
            return original.IsFailure ? original.Error : child.Error;
        }

        var childTransaction = child.Value!;
        var attempt = await StartAttemptAsync(session, childTransaction, childTransaction.Routing!.CurrentProvider, cancellationToken);
        if (attempt.IsFailure)
        {
            return attempt.Error;
        }

        var originalAttempts = await attemptStore.GetByTransactionAsync(session, originalId, cancellationToken);
        if (originalAttempts.IsFailure)
        {
            return originalAttempts.Error;
        }

        var productCode = await referenceData.FindProductCodeAsync(session, childTransaction.ProductId, cancellationToken);
        if (productCode is null)
        {
            return new RansysError(ErrorCodes.ReferenceDataNotFound, ErrorCategory.Internal, $"Product {childTransaction.ProductId} does not exist.");
        }

        return new Prepared(
            childTransaction, attempt.Value, original.Value,
            OriginalProviderReferences.From(original.Value!, originalAttempts.Value), productCode, null, IsReplay: false);
    }

    private static RansysError OriginalInvalid() =>
        RansysError.Validation(
            ProcessingErrorCodes.OriginalTransactionInvalid, "originalTransactionId does not identify a transaction of this client.", "originalTransactionId");

    // ------------------------------------------------------------------ shared session-1 helpers

    /// <summary>Session 1: commits only on success. Any database failure becomes <see cref="FinancialDependencyUnavailableException"/>.</summary>
    private async Task<Result<Prepared>> InDurableSessionAsync(Func<IDatabaseSession, Task<Result<Prepared>>> work, CancellationToken cancellationToken)
    {
        try
        {
            await using var session = await sessions.BeginAsync(cancellationToken);
            var prepared = await work(session);
            if (prepared.IsSuccess)
            {
                await session.CommitAsync(cancellationToken);
            }

            return prepared;
        }
        catch (Exception ex) when (IsDependencyFailure(ex))
        {
            throw new FinancialDependencyUnavailableException(
                "The Transaction DB is unavailable; the request was not processed and no provider was called.", ex);
        }
    }

    private async Task<Result<Prepared>> ReplayAsync(
        IDatabaseSession session, TransactionId id, TransactionType expectedType, TransactionId? expectedOriginal, CancellationToken cancellationToken)
    {
        var existing = await LoadAsync(session, id, cancellationToken);
        if (existing.IsFailure)
        {
            return existing.Error;
        }

        // The fingerprint does not cover the original id; a reused reference for another original is a conflict, never
        // the other original's result.
        var transaction = existing.Value;
        if (transaction.Type != expectedType || transaction.Identity.OriginalTransactionId != expectedOriginal)
        {
            return RansysError.Conflict(
                ErrorCodes.DuplicateReferenceConflict, $"Client reference '{transaction.Identity.ClientReference}' was already used for another request.");
        }

        var history = await attemptStore.GetByTransactionAsync(session, id, cancellationToken);
        if (history.IsFailure)
        {
            return history.Error;
        }

        var latest = history.Value.Where(a => a.Outcome is not null).OrderByDescending(a => a.AttemptNumber).Select(a => a.Outcome).FirstOrDefault();
        return new Prepared(transaction, null, null, OriginalProviderReferences.None, string.Empty, latest, IsReplay: true);
    }

    private async Task<Result<Transaction>> LoadAsync(IDatabaseSession session, TransactionId id, CancellationToken cancellationToken)
    {
        var loaded = await transactions.GetAsync(session, id, forUpdate: false, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        return loaded.Value is { } transaction
            ? transaction
            : new RansysError(ErrorCodes.PersistedStateInvalid, ErrorCategory.Internal, $"Transaction {id} disappeared.");
    }

    private Task<Result<TransactionAttempt>> StartAttemptAsync(
        IDatabaseSession session, Transaction transaction, ProviderReference provider, CancellationToken cancellationToken)
    {
        var correlation = transaction.Id.ToString();
        var trace = Activity.Current?.TraceId.ToHexString() is { } traceId && traceId != new string('0', 32) ? traceId : correlation;
        return attempts.StartAsync(
            session, transaction, Transaction.PrimaryAttemptTypeFor(transaction.Type)!.Value, provider, correlation, trace, cancellationToken);
    }

    /// <summary>Ends a transaction before any provider request (FAILED; an active reservation is released).</summary>
    private async Task<Result> FailBeforeSendAsync(
        IDatabaseSession session, TransactionId id, string reasonCode, string? responseCode, string? description, AttemptId? attemptId, CancellationToken cancellationToken)
    {
        var failed = await finalization.ApplyAsync(
            session,
            new ProviderResultCommand(id, AttemptResolutionKind.Failed, ChangeSource.Core, reasonCode, responseCode, attemptId, Truncate(description)),
            cancellationToken);
        return failed.IsFailure ? failed.Error : Result.Success();
    }

    // ------------------------------------------------------------------ provider call + session 2

    private async Task<Result<TransactionProcessingResult>> CompleteAsync(Prepared prepared)
    {
        if (prepared.Attempt is null)
        {
            // Replay (R4/ADR-026, ADR-027): prefer the transaction's latest-provider-result projection (a later/final
            // async report, e.g. a callback that arrived after the resolving attempt's outcome was already immutable)
            // over the latest resolved attempt's own data, so a merchant retry never loses business data to either.
            var data = prepared.Transaction.LatestProviderResult?.Evidence.Data ?? prepared.LatestOutcome?.Data ?? NoData;
            return Build(prepared.Transaction, prepared.LatestOutcome, data, prepared.IsReplay);
        }

        var transaction = prepared.Transaction;
        var attempt = prepared.Attempt;
        while (true)
        {
            var request = requestFactory.Create(transaction, attempt, prepared.ProductCode, prepared.Original, prepared.OriginalReferences);
            var result = await invoker.InvokeAsync(attempt.Provider, transaction.Type, attempt.AttemptType, request, CancellationToken.None);
            var interpreted = ProviderResultInterpreter.Interpret(result, clock.UtcNow);
            var data = interpreted.Result.Data ?? NoData;

            var step = await RecordAsync(transaction, attempt, interpreted);
            switch (step)
            {
                case Next next:
                    transaction = next.Transaction;
                    attempt = next.Attempt;
                    continue;

                case Completed completed:
                    return Build(completed.Transaction, interpreted.Outcome, data, isReplay: false);

                default:
                    // Session 2 could not be written: the provider may have acted, so the honest answer is IN_DOUBT.
                    return new TransactionProcessingResult(
                        transaction.Id, transaction.Identity.ClientReference, transaction.Type, ProcessingStatus.InDoubt,
                        RansysResponseCodes.InDoubt, RansysResponseCodes.MessageFor(RansysResponseCodes.InDoubt, ProcessingStatus.InDoubt),
                        new PublicReferences(transaction.References.MerchantReference, interpreted.Outcome.ProviderStan, interpreted.Outcome.ProviderRrn),
                        data, clock.UtcNow, IsReplay: false);
            }
        }
    }

    private async Task<Step> RecordAsync(Transaction transaction, TransactionAttempt attempt, InterpretedProviderResult interpreted)
    {
        try
        {
            await using (var session = await sessions.BeginAsync(CancellationToken.None))
            {
                var step = await RecordInSessionAsync(session, transaction, attempt.Id, interpreted);
                if (step.IsSuccess)
                {
                    await session.CommitAsync(CancellationToken.None);
                    return step.Value;
                }
            }

            // The result could not be applied (e.g. the ledger refused the posting). Keep the truth conservative and
            // visible: record the outcome and mark the transaction IN_DOUBT (hold kept) for status check / reconciliation.
            await using var fallback = await sessions.BeginAsync(CancellationToken.None);
            var marked = await MarkInDoubtAsync(fallback, transaction, attempt.Id, interpreted);
            if (marked.IsSuccess)
            {
                await fallback.CommitAsync(CancellationToken.None);
                return new Completed(marked.Value);
            }

            return Unrecorded.Instance;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Unrecorded.Instance;
        }
    }

    private async Task<Result<Step>> RecordInSessionAsync(
        IDatabaseSession session, Transaction transaction, AttemptId attemptId, InterpretedProviderResult interpreted)
    {
        var locked = await LockAsync(session, transaction);
        if (locked.IsFailure)
        {
            return locked.Error;
        }

        var current = locked.Value;
        var history = await attemptStore.GetByTransactionAsync(session, current.Id, CancellationToken.None);
        if (history.IsFailure)
        {
            return history.Error;
        }

        var attempt = history.Value.Single(a => a.Id == attemptId);
        var resolution = interpreted.Resolution;
        if (!attempt.IsOutcomeRecorded)
        {
            var recorded = await attempts.RecordOutcomeAsync(session, attempt, interpreted.Outcome, CancellationToken.None);
            if (recorded.IsFailure)
            {
                return recorded.Error;
            }
        }
        else if (attempt.Outcome != interpreted.Outcome && resolution == AttemptResolutionKind.NotSent)
        {
            // Another path (recovery) already recorded this attempt as possibly sent: never fail over.
            resolution = AttemptResolutionKind.InDoubt;
        }

        if (resolution == AttemptResolutionKind.NotSent)
        {
            return await FailoverAsync(session, current, attempt, history.Value);
        }

        var applied = await finalization.ApplyAsync(
            session,
            new ProviderResultCommand(
                current.Id, resolution, ChangeSource.SyncProviderResponse, interpreted.ReasonCode,
                resolution == AttemptResolutionKind.InDoubt ? RansysResponseCodes.InDoubt : interpreted.ResponseCode, attempt.Id,
                Evidence: interpreted.Outcome),
            CancellationToken.None);
        if (applied.IsFailure)
        {
            return applied.Error;
        }

        var reloaded = await LoadAsync(session, current.Id, CancellationToken.None);
        return reloaded.IsFailure ? reloaded.Error : new Completed(reloaded.Value);
    }

    /// <summary>
    /// Proven NOT_SENT: pre-send failover to the next eligible provider (ADR-005), bounded; otherwise FAILED + release (5001).
    /// Children never fail over (they must reach the original's provider).
    /// </summary>
    private async Task<Result<Step>> FailoverAsync(
        IDatabaseSession session, Transaction transaction, TransactionAttempt attempt, IReadOnlyList<TransactionAttempt> history)
    {
        var tried = history.Where(a => a.AttemptType == attempt.AttemptType).Select(a => a.Provider.ProviderId).Distinct().ToList();
        if (!TransactionTypeRules.RequiresOriginalTransaction(transaction.Type) && tried.Count < options.MaxProviderAttempts)
        {
            var route = await routing.RouteAsync(
                session,
                new RoutingRequest(transaction.Id, transaction.Type, transaction.ProductId, transaction.MerchantId, transaction.ChannelId, transaction.Amount, tried),
                CancellationToken.None);
            if (route.IsSuccess)
            {
                var failover = transaction.RecordFailover(attempt, route.Value.SelectedProvider, ProviderResultInterpreter.ReasonNotSent, clock.UtcNow);
                if (failover.IsFailure)
                {
                    return failover.Error;
                }

                var saved = await transactions.UpdateAsync(session, transaction, CancellationToken.None);
                if (saved.IsFailure)
                {
                    return saved.Error;
                }

                var next = await StartAttemptAsync(session, transaction, route.Value.SelectedProvider, CancellationToken.None);
                return next.IsFailure ? next.Error : new Next(transaction, next.Value);
            }

            if (route.Error.Code is not (ErrorCodes.NoRouteAvailable or ErrorCodes.ConfigurationNotAvailable))
            {
                return route.Error;
            }
        }

        var failed = await FailBeforeSendAsync(
            session, transaction.Id, ErrorCodes.NoRouteAvailable, RansysResponseCodes.NoRouteAvailable,
            $"No provider accepted the request; {tried.Count} provider(s) proved not sent.", attempt.Id, CancellationToken.None);
        if (failed.IsFailure)
        {
            return failed.Error;
        }

        var reloaded = await LoadAsync(session, transaction.Id, CancellationToken.None);
        return reloaded.IsFailure ? reloaded.Error : new Completed(reloaded.Value);
    }

    private async Task<Result<Transaction>> MarkInDoubtAsync(
        IDatabaseSession session, Transaction transaction, AttemptId attemptId, InterpretedProviderResult interpreted)
    {
        var locked = await LockAsync(session, transaction);
        if (locked.IsFailure)
        {
            return locked.Error;
        }

        var history = await attemptStore.GetByTransactionAsync(session, transaction.Id, CancellationToken.None);
        if (history.IsFailure)
        {
            return history.Error;
        }

        var attempt = history.Value.Single(a => a.Id == attemptId);
        if (!attempt.IsOutcomeRecorded)
        {
            var recorded = await attempts.RecordOutcomeAsync(session, attempt, interpreted.Outcome, CancellationToken.None);
            if (recorded.IsFailure)
            {
                return recorded.Error;
            }
        }

        var marked = await finalization.ApplyAsync(
            session,
            new ProviderResultCommand(
                transaction.Id, AttemptResolutionKind.InDoubt, ChangeSource.SyncProviderResponse, ReasonFinalizationFailed,
                RansysResponseCodes.InDoubt, attempt.Id, $"Provider result {interpreted.ReasonCode} could not be applied.",
                Evidence: interpreted.Outcome),
            CancellationToken.None);

        // A transaction that is already final cannot go IN_DOUBT; the recorded outcome is kept for reconciliation.
        if (marked.IsFailure && marked.Error.Code != ErrorCodes.InvalidStateTransition)
        {
            return marked.Error;
        }

        return await LoadAsync(session, transaction.Id, CancellationToken.None);
    }

    /// <summary>Lock order parent → child, then returns the (locked) transaction.</summary>
    private async Task<Result<Transaction>> LockAsync(IDatabaseSession session, Transaction transaction)
    {
        if (transaction.Identity.OriginalTransactionId is { } originalId)
        {
            var parent = await transactions.GetAsync(session, originalId, forUpdate: true, CancellationToken.None);
            if (parent.IsFailure)
            {
                return parent.Error;
            }
        }

        var locked = await transactions.GetAsync(session, transaction.Id, forUpdate: true, CancellationToken.None);
        if (locked.IsFailure)
        {
            return locked.Error;
        }

        return locked.Value ?? (Result<Transaction>)new RansysError(ErrorCodes.PersistedStateInvalid, ErrorCategory.Internal, $"Transaction {transaction.Id} disappeared.");
    }

    // ------------------------------------------------------------------ mapping

    private TransactionProcessingResult Build(
        Transaction transaction, AttemptOutcome? attemptOutcome, IReadOnlyDictionary<string, JsonElement> fallbackData, bool isReplay)
    {
        // T3 (ADR-027): one accepted-result selection feeds both references and data, so a replayed POST of the same
        // idempotent request shows the same STAN/RRN/data as GET — the transaction's latest accepted provider evidence,
        // never a stale attempt's own outcome once a later report has superseded it.
        var accepted = transaction.LatestProviderResult?.Evidence ?? attemptOutcome;
        var data = accepted?.Data ?? fallbackData;
        var code = RansysResponseCodes.ForTransaction(transaction.ProcessingStatus, transaction.ResponseCode);
        return new TransactionProcessingResult(
            transaction.Id,
            transaction.Identity.ClientReference,
            transaction.Type,
            transaction.ProcessingStatus,
            code,
            RansysResponseCodes.MessageFor(code, transaction.ProcessingStatus),
            new PublicReferences(
                transaction.References.MerchantReference,
                accepted?.ProviderStan ?? transaction.References.Stan,
                accepted?.ProviderRrn ?? transaction.References.Rrn),
            data,
            clock.UtcNow,
            isReplay);
    }

    private static Result<Customer?> ToCustomer(CustomerInput? input)
    {
        if (input is null)
        {
            return Result<Customer?>.Success(null);
        }

        var metadata = ToMetadata(input.Metadata, "customer.metadata");
        if (metadata.IsFailure)
        {
            return metadata.Error;
        }

        var customer = Customer.Create(input.CustomerId, input.ExternalCustomerReference, input.AccountNumber, input.PhoneNumber, input.Name, metadata.Value);
        return customer.IsSuccess ? customer.Value : customer.Error;
    }

    private static Result<TransactionEndpoint?> ToEndpoint(EndpointInput? input, string field)
    {
        if (input is null)
        {
            return Result<TransactionEndpoint?>.Success(null);
        }

        var metadata = ToMetadata(input.Metadata, field + ".metadata");
        if (metadata.IsFailure)
        {
            return metadata.Error;
        }

        var endpoint = TransactionEndpoint.Create(input.Type, input.Identifier, input.InstitutionCode, input.AccountReference, metadata.Value);
        return endpoint.IsSuccess ? endpoint.Value : endpoint.Error with { Field = field + "." + endpoint.Error.Field?.Replace("endpoint.", string.Empty, StringComparison.Ordinal) };
    }

    private static Result<ExtensionMetadata> ToMetadata(IReadOnlyDictionary<string, JsonElement>? values, string field)
    {
        if (values is null || values.Count == 0)
        {
            return ExtensionMetadata.Empty;
        }

        var metadata = ExtensionMetadata.Create(values);
        return metadata.IsSuccess ? metadata.Value : metadata.Error with { Field = $"{field}.{metadata.Error.Field}" };
    }

    private static TransitionContext Context(string reason, DateTimeOffset now) =>
        TransitionContext.Create(reason, ChangeSource.Core, now).Value;

    private static string? Truncate(string? text) =>
        text is { Length: > TransitionContext.MaxReasonDescriptionLength } ? text[..TransitionContext.MaxReasonDescriptionLength] : text;

    /// <summary>
    /// Database/driver failures (connection, timeout, serialization) mean "durable dependency unavailable". Integrity
    /// violations (SQLSTATE class 23) are programming or data errors and propagate unchanged.
    /// </summary>
    private static bool IsDependencyFailure(Exception ex) => ex switch
    {
        DbException db => db.SqlState is not { } state || !state.StartsWith("23", StringComparison.Ordinal),
        TimeoutException or System.Net.Sockets.SocketException or IOException => true,
        _ => false,
    };

    private sealed record OriginalRequest(
        ChannelId ChannelId,
        MerchantId MerchantId,
        TransactionType Type,
        string ClientReference,
        string? IdempotencyKey,
        string ProductCode,
        MoneyInput? Amount,
        CustomerInput? Customer,
        EndpointInput? Source,
        EndpointInput? Destination,
        string? MerchantReference,
        IReadOnlyDictionary<string, JsonElement>? Metadata);

    private sealed record ChildRequest(
        ChannelId ChannelId,
        MerchantId MerchantId,
        TransactionType Type,
        string ClientReference,
        string? IdempotencyKey,
        Guid OriginalTransactionId,
        MoneyInput? RefundAmount,
        string Reason,
        IReadOnlyDictionary<string, JsonElement>? Metadata);

    /// <summary>State after session 1: the attempt to send (null when nothing is sent).</summary>
    private sealed record Prepared(
        Transaction Transaction,
        TransactionAttempt? Attempt,
        Transaction? Original,
        OriginalProviderReferences OriginalReferences,
        string ProductCode,
        AttemptOutcome? LatestOutcome,
        bool IsReplay);

    private abstract record Step;

    private sealed record Next(Transaction Transaction, TransactionAttempt Attempt) : Step;

    private sealed record Completed(Transaction Transaction) : Step;

    private sealed record Unrecorded : Step
    {
        public static Unrecorded Instance { get; } = new();
    }
}
