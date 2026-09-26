// Provider Adapter Contract v1 (docs/RANSYS_Provider_Adapter_Contract_v1.md).
// Shapes are copied verbatim from docs/RANSYS_Provider_Adapter_Contracts_v1.cs; only documentation
// comments are added here. Do not change a shape without a new contract version.
using System.Text.Json;

namespace Ransys.Adapter.Contracts.V1;

/// <summary>
/// Semantic Core-to-adapter contract (§3). Implemented in-process (Lite) or remotely through the gRPC binding in
/// <c>Ransys.Adapter.Sdk</c>; both bindings share identical semantics (§27.10). Adapters never retry a financial
/// request after a possible send (§12) and report <see cref="ProviderTransportResult.RequestSent"/> conservatively (§10).
/// </summary>
public interface IProviderAdapter
{
    Task<ProviderCapabilities> GetCapabilitiesAsync(ProviderIdentity provider, CancellationToken cancellationToken);
    Task<ProviderHealthResult> HealthCheckAsync(ProviderIdentity provider, CancellationToken cancellationToken);

    Task<ProviderResult> InquiryAsync(ProviderTransactionRequest request, CancellationToken cancellationToken);
    Task<ProviderResult> PaymentAsync(ProviderTransactionRequest request, CancellationToken cancellationToken);
    Task<ProviderResult> PurchaseAsync(ProviderTransactionRequest request, CancellationToken cancellationToken);
    Task<ProviderResult> TransferAsync(ProviderTransactionRequest request, CancellationToken cancellationToken);
    Task<ProviderResult> VoidAsync(ProviderTransactionRequest request, CancellationToken cancellationToken);
    Task<ProviderResult> StatusCheckAsync(ProviderTransactionRequest request, CancellationToken cancellationToken);
    Task<ProviderResult> ReversalAsync(ProviderTransactionRequest request, CancellationToken cancellationToken);
    Task<ProviderResult> RefundAsync(ProviderTransactionRequest request, CancellationToken cancellationToken);
    Task<ProviderResult> AdviceAsync(ProviderTransactionRequest request, CancellationToken cancellationToken);
    Task<ProviderResult> BalanceInquiryAsync(ProviderTransactionRequest request, CancellationToken cancellationToken);

    Task<ProviderBalanceResult> GetProviderBalanceAsync(
        ProviderIdentity provider,
        string? balanceAccountReference,
        CancellationToken cancellationToken);
}

/// <summary>Core-side sink for normalized provider callbacks (§15). Callbacks are at-least-once.</summary>
public interface IProviderCallbackSink
{
    Task<ProviderCallbackAck> SubmitAsync(ProviderCallback callback, CancellationToken cancellationToken);
}

/// <summary>Normalized outcome (§8). Valid pairs with <see cref="ResultFinality"/> are enforced by <c>ProviderResultRules</c>.</summary>
public enum ProviderOutcome { Success, Failed, Pending, InDoubt, NotSent }
/// <summary>Finality of an outcome (§9).</summary>
public enum ResultFinality { Definitive, NonFinal, Ambiguous, NotApplicable }
/// <summary>Transport fact (§11); never equal to financial status. Wire name of <see cref="NotSent"/> is <c>NOT_SENT_TRANSPORT</c>.</summary>
public enum TransportStatus { NotSent, Sent, Response, Timeout, ConnectionError, ProtocolError }

public sealed record ProviderIdentity(Guid ProviderId, string ProviderCode, string AdapterService);

public sealed record ProviderCapabilities(IReadOnlySet<string> CapabilityCodes, string ContractVersion);

public sealed record ProviderTimeoutPolicy(
    TimeSpan ConnectTimeout,
    TimeSpan ReadTimeout,
    int MaxRetry,
    TimeSpan RetryDelay,
    string RetryBackoff,
    bool StatusCheckAfterTimeout,
    bool ReversalAfterTimeout);

public sealed record ProviderConcurrencyPolicy(
    int? MaxConcurrentRequests,
    int? MaxQueueDepth,
    int? RateLimitPerSecond);

public sealed record ProviderExecutionContext(
    ProviderIdentity Provider,
    Guid? EndpointProfileId,
    long? ProductMappingVersion,
    ProviderTimeoutPolicy TimeoutPolicy,
    ProviderConcurrencyPolicy ConcurrencyPolicy,
    IReadOnlySet<string> Capabilities,
    string? AuthenticationProfileReference,
    long PolicyVersion);

public sealed record ProviderTransactionReferences(
    string ClientReference,
    string? MerchantReference,
    string? Stan,
    string? Rrn,
    string? ProviderReference,
    string? ProviderStan,
    string? ProviderRrn,
    IReadOnlyDictionary<string, string> ExternalReferences);

public sealed record CorrelationContext(Guid RansysTransactionId, string CorrelationId, string TraceId);

public sealed record ProviderTransactionRequest(
    Guid RansysTransactionId,
    Guid? OriginalTransactionId,
    Guid AttemptId,
    string TransactionType,
    string ProductCode,
    decimal? Amount,
    string? CurrencyCode,
    int? CurrencyDefinitionVersion,
    IReadOnlyDictionary<string, JsonElement> Customer,
    IReadOnlyDictionary<string, JsonElement> Source,
    IReadOnlyDictionary<string, JsonElement> Destination,
    ProviderTransactionReferences References,
    ProviderExecutionContext ExecutionContext,
    CorrelationContext Correlation,
    IReadOnlyDictionary<string, JsonElement> Metadata);

public sealed record ProviderError(
    string Category,
    string Code,
    string Message,
    bool RetryableTransportError,
    string? RawCode);

public sealed record ProviderTransportResult(
    TransportStatus Status,
    bool RequestSent,
    TimeSpan? ConnectDuration,
    TimeSpan? RoundTripDuration,
    ProviderError? Error);

public sealed record ProviderRetryHint(bool SafeToRetryTransport, TimeSpan? SuggestedDelay);

/// <summary>Normalized provider result (§7).</summary>
public sealed record ProviderResult(
    ProviderOutcome Outcome,
    ResultFinality Finality,
    ProviderTransportResult Transport,
    string RansysResponseCode,
    string? ProviderResponseCode,
    string? ProviderResponseMessage,
    ProviderTransactionReferences References,
    IReadOnlyDictionary<string, JsonElement> Data,
    ProviderRetryHint? RetryHint,
    DateTimeOffset ReceivedAt,
    string? RawRequestReference,
    string? RawResponseReference);

public sealed record ProviderHealthResult(
    string State,
    int? LatencyMs,
    string? DiagnosticCode,
    string? DiagnosticMessage,
    DateTimeOffset ObservedAt);

public sealed record ProviderBalanceResult(
    decimal Balance,
    string CurrencyCode,
    int CurrencyDefinitionVersion,
    DateTimeOffset ObservedAt,
    string Source);

public sealed record ProviderCallback(
    Guid ProviderId,
    string? CallbackId,
    Guid OriginalRansysTransactionId,
    string? ProviderReference,
    ProviderResult Result,
    CorrelationContext Correlation,
    DateTimeOffset ReceivedAt);

public sealed record ProviderCallbackAck(bool Accepted, string? AcknowledgementCode);
