using System.Collections.Immutable;
using Ransys.Adapter.Contracts.V1;
using Ransys.Application;
using Ransys.Domain.Attempts;
using Ransys.Domain.Routing;
using Ransys.Domain.Transactions;
using V1Transport = Ransys.Adapter.Contracts.V1.TransportStatus;

namespace Ransys.TransactionCore.Providers;

/// <summary>
/// Sends one attempt to the provider's adapter, outside any database transaction (Architecture Spec §12). It never
/// throws for adapter behavior and never retries:
/// <list type="bullet">
/// <item>no adapter registered for the provider ⇒ NOT_SENT, <c>requestSent = false</c>: nothing was called, so
/// non-delivery is proven and pre-send failover may follow;</item>
/// <item>the adapter throws, returns null, or exceeds the time budget after the call started ⇒ IN_DOUBT,
/// <c>requestSent = true</c> (possibly sent; never failed over, ADR-005);</item>
/// <item>otherwise the adapter's own result is returned unchanged (the interpreter normalizes it).</item>
/// </list>
/// Dispatch follows the transaction type: TRANSFER → <see cref="IProviderAdapter.TransferAsync"/> and VOID →
/// <see cref="IProviderAdapter.VoidAsync"/>; VOID is never mapped to reversal or refund (ADR-017, ADR-019).
/// </summary>
public sealed class ProviderInvoker(IProviderAdapterResolver resolver, IClock clock, ProviderCallPolicy policy)
{
    public const string AdapterNotRegisteredCode = "ADAPTER_NOT_REGISTERED";
    public const string AdapterExceptionCode = "ADAPTER_EXCEPTION";
    public const string AdapterTimeoutCode = "ADAPTER_CALL_TIMEOUT";
    public const string AdapterNoResultCode = "ADAPTER_RETURNED_NO_RESULT";

    public ProviderInvoker(IProviderAdapterResolver resolver, IClock clock)
        : this(resolver, clock, ProviderCallPolicy.Default)
    {
    }

    /// <summary>
    /// Calls the adapter method for <paramref name="transactionType"/> / <paramref name="attemptType"/>.
    /// The attempt is already committed as possibly sent, so a cancellation of <paramref name="cancellationToken"/> is
    /// forwarded to the adapter and reported as an ambiguous outcome like a timeout; it is never thrown.
    /// </summary>
    public async Task<ProviderResult> InvokeAsync(
        ProviderReference provider,
        TransactionType transactionType,
        AttemptType attemptType,
        ProviderTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);

        var operation = OperationFor(transactionType, attemptType);
        var adapter = resolver.Resolve(provider.ProviderId);
        if (adapter is null)
        {
            return ProviderResults.NotSent(
                V1Transport.NotSent,
                new ProviderError(
                    ProviderErrorCategories.Connection, AdapterNotRegisteredCode,
                    $"No adapter binding is registered for provider {provider.ProviderCode}.", RetryableTransportError: false, RawCode: null),
                ProviderResultCodes.InternalError,
                request.References,
                clock.UtcNow);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(policy.Budget);
        Task<ProviderResult> call;
        try
        {
            call = operation(adapter, request, timeout.Token);
        }
        catch (Exception ex)
        {
            return InDoubt(V1Transport.Sent, AdapterExceptionCode, $"Adapter threw: {ex.GetType().Name}.", request);
        }

        try
        {
            // WaitAsync bounds the wait even if the adapter ignores its cancellation token.
            var result = await call.WaitAsync(policy.Budget, CancellationToken.None);
            return result ?? InDoubt(V1Transport.Sent, AdapterNoResultCode, "The adapter returned no result.", request);
        }
        catch (TimeoutException)
        {
            return InDoubt(V1Transport.Timeout, AdapterTimeoutCode, $"No adapter result within {policy.Budget}.", request);
        }
        catch (OperationCanceledException)
        {
            return InDoubt(V1Transport.Timeout, AdapterTimeoutCode, "The adapter call was cancelled after it started.", request);
        }
        catch (Exception ex)
        {
            return InDoubt(V1Transport.Sent, AdapterExceptionCode, $"Adapter threw: {ex.GetType().Name}.", request);
        }
    }

    /// <summary>The adapter operation for a transaction's primary request (Provider Adapter Contract v1 §3).</summary>
    public static Func<IProviderAdapter, ProviderTransactionRequest, CancellationToken, Task<ProviderResult>> OperationFor(
        TransactionType transactionType, AttemptType attemptType) => (transactionType, attemptType) switch
        {
            (TransactionType.Payment, AttemptType.Payment) => (a, r, ct) => a.PaymentAsync(r, ct),
            (TransactionType.Purchase, AttemptType.Payment) => (a, r, ct) => a.PurchaseAsync(r, ct),
            (TransactionType.Transfer, AttemptType.Transfer) => (a, r, ct) => a.TransferAsync(r, ct),
            (TransactionType.Inquiry, AttemptType.Inquiry) => (a, r, ct) => a.InquiryAsync(r, ct),
            (TransactionType.BalanceInquiry, AttemptType.BalanceInquiry) => (a, r, ct) => a.BalanceInquiryAsync(r, ct),
            (TransactionType.Refund, AttemptType.Refund) => (a, r, ct) => a.RefundAsync(r, ct),
            (TransactionType.Reversal, AttemptType.Reversal) => (a, r, ct) => a.ReversalAsync(r, ct),
            (TransactionType.Void, AttemptType.Void) => (a, r, ct) => a.VoidAsync(r, ct),
            (_, AttemptType.StatusCheck) => (a, r, ct) => a.StatusCheckAsync(r, ct),
            (_, AttemptType.Advice) => (a, r, ct) => a.AdviceAsync(r, ct),
            _ => throw new ArgumentOutOfRangeException(
                nameof(attemptType), $"{attemptType} is not the provider request of a {transactionType} transaction."),
        };

    private ProviderResult InDoubt(V1Transport status, string code, string message, ProviderTransactionRequest request) =>
        ProviderResults.InDoubt(
            status,
            new ProviderError(ProviderErrorCategories.Unknown, code, message, RetryableTransportError: false, RawCode: null),
            request.References ?? new ProviderTransactionReferences(
                string.Empty, null, null, null, null, null, null, ImmutableDictionary<string, string>.Empty),
            clock.UtcNow);
}
