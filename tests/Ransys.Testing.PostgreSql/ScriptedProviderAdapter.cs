using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Ransys.Adapter.Contracts.V1;

namespace Ransys.Testing;

/// <summary>
/// In-process test adapter (Provider Adapter Contract v1). Each call takes the next scripted step (FIFO); when the script
/// is empty it answers <see cref="Success"/>. Every call is recorded with its operation name and request.
/// </summary>
public sealed class ScriptedProviderAdapter : IProviderAdapter
{
    public delegate Task<ProviderResult> Step(string operation, ProviderTransactionRequest request, CancellationToken cancellationToken);

    private readonly ConcurrentQueue<Step> _script = new();
    private readonly ConcurrentQueue<(string Operation, ProviderTransactionRequest Request)> _calls = new();

    public IReadOnlyList<(string Operation, ProviderTransactionRequest Request)> Calls => [.. _calls];

    public int CallCount => _calls.Count;

    public ScriptedProviderAdapter Then(Step step)
    {
        _script.Enqueue(step);
        return this;
    }

    public ScriptedProviderAdapter ThenResult(Func<ProviderTransactionRequest, ProviderResult> result) =>
        Then((_, request, _) => Task.FromResult(result(request)));

    public ScriptedProviderAdapter ThenSuccess(string? providerReference = null, string? rrn = null) =>
        ThenResult(r => Success(r, providerReference: providerReference, rrn: rrn));

    public ScriptedProviderAdapter ThenDeclined(string responseCode = "1001") => ThenResult(r => Declined(r, responseCode));

    public ScriptedProviderAdapter ThenPending() => ThenResult(Pending);

    public ScriptedProviderAdapter ThenNotSent() => ThenResult(NotSent);

    public ScriptedProviderAdapter ThenThrow() => Then((_, _, _) => throw new InvalidOperationException("adapter crashed"));

    /// <summary>Never answers within the Core budget; honors cancellation.</summary>
    public ScriptedProviderAdapter ThenHang() => Then(async (_, _, ct) =>
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("unreachable");
    });

    public static ProviderResult Success(
        ProviderTransactionRequest request, string code = "0000", string? providerReference = null, string? rrn = null,
        IReadOnlyDictionary<string, JsonElement>? data = null) =>
        Response(request, ProviderOutcome.Success, ResultFinality.Definitive, code, "00", providerReference ?? $"PRV-{request.AttemptId:N}", rrn, data);

    public static ProviderResult Declined(ProviderTransactionRequest request, string code = "1001") =>
        Response(request, ProviderOutcome.Failed, ResultFinality.Definitive, code, "51", null, null, null);

    public static ProviderResult Pending(ProviderTransactionRequest request) =>
        Response(request, ProviderOutcome.Pending, ResultFinality.NonFinal, "1002", "09", null, null, null);

    public static ProviderResult NotSent(ProviderTransactionRequest request) =>
        ProviderResults.NotSent(
            TransportStatus.ConnectionError,
            new ProviderError(ProviderErrorCategories.Connection, "CONNECT_REFUSED", "connect refused", true, null),
            ProviderResultCodes.ProviderLinkDown,
            request.References,
            DateTimeOffset.UtcNow);

    public static ProviderResult Response(
        ProviderTransactionRequest request, ProviderOutcome outcome, ResultFinality finality, string code, string? providerCode,
        string? providerReference, string? rrn, IReadOnlyDictionary<string, JsonElement>? data) =>
        new(
            outcome,
            finality,
            new ProviderTransportResult(TransportStatus.Response, RequestSent: true, TimeSpan.FromMilliseconds(3), TimeSpan.FromMilliseconds(20), null),
            code,
            providerCode,
            $"provider says {outcome}",
            request.References with { ProviderReference = providerReference, ProviderRrn = rrn },
            data ?? ImmutableDictionary<string, JsonElement>.Empty,
            RetryHint: null,
            DateTimeOffset.UtcNow,
            RawRequestReference: null,
            RawResponseReference: null);

    public Task<ProviderCapabilities> GetCapabilitiesAsync(ProviderIdentity provider, CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderCapabilities(ProviderCapabilityCodes.All.ToImmutableHashSet(), "1.0"));

    public Task<ProviderHealthResult> HealthCheckAsync(ProviderIdentity provider, CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderHealthResult("HEALTHY", 1, null, null, DateTimeOffset.UtcNow));

    public Task<ProviderResult> InquiryAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) => Run(nameof(InquiryAsync), request, cancellationToken);

    public Task<ProviderResult> PaymentAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) => Run(nameof(PaymentAsync), request, cancellationToken);

    public Task<ProviderResult> PurchaseAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) => Run(nameof(PurchaseAsync), request, cancellationToken);

    public Task<ProviderResult> TransferAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) => Run(nameof(TransferAsync), request, cancellationToken);

    public Task<ProviderResult> VoidAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) => Run(nameof(VoidAsync), request, cancellationToken);

    public Task<ProviderResult> StatusCheckAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) => Run(nameof(StatusCheckAsync), request, cancellationToken);

    public Task<ProviderResult> ReversalAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) => Run(nameof(ReversalAsync), request, cancellationToken);

    public Task<ProviderResult> RefundAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) => Run(nameof(RefundAsync), request, cancellationToken);

    public Task<ProviderResult> AdviceAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) => Run(nameof(AdviceAsync), request, cancellationToken);

    public Task<ProviderResult> BalanceInquiryAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) => Run(nameof(BalanceInquiryAsync), request, cancellationToken);

    public Task<ProviderBalanceResult> GetProviderBalanceAsync(ProviderIdentity provider, string? balanceAccountReference, CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderBalanceResult(0m, "IDR", 1, DateTimeOffset.UtcNow, "TEST"));

    private Task<ProviderResult> Run(string operation, ProviderTransactionRequest request, CancellationToken cancellationToken)
    {
        _calls.Enqueue((operation, request));
        return _script.TryDequeue(out var step)
            ? step(operation, request, cancellationToken)
            : Task.FromResult(Success(request));
    }
}
