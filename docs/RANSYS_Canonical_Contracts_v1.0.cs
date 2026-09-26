// RANSYS Canonical Contracts v1.0
// Reference contracts only. Production domain aggregates should enforce invariants
// through factories and behavior methods rather than unrestricted construction.

using System.Collections.Generic;
using System.Text.Json;

namespace Ransys.Contracts.Canonical;

public readonly record struct TransactionId(Guid Value);
public readonly record struct MerchantId(Guid Value);
public readonly record struct ChannelId(Guid Value);
public readonly record struct ProductId(Guid Value);
public readonly record struct ProviderId(Guid Value);
public readonly record struct WalletId(Guid Value);
public readonly record struct AttemptId(Guid Value);

public enum TransactionType
{
    Inquiry,
    Payment,
    Purchase,
    Transfer,
    Refund,
    Reversal,
    Void,
    Advice,
    BalanceInquiry,
    StatusCheck,
    Settlement,
    TopUp,
    Adjustment
}

public enum ProcessingStatus
{
    Received,
    Validated,
    Processing,
    Pending,
    InDoubt,
    Success,
    Failed,
    ReversalPending,
    Reversed,
    RefundPending,
    PartiallyRefunded,
    Refunded
}

public enum FinancialStatus
{
    None,
    Reserved,
    Posted,
    Released,
    ReversalPending,
    Reversed,
    RefundPending,
    PartiallyRefunded,
    Refunded,
    Adjusted
}

public enum ReconciliationStatus
{
    Unmatched,
    Pending,
    Matched,
    Exception,
    Resolved
}

public enum SettlementStatus
{
    NotApplicable,
    Pending,
    Included,
    Approved,
    ReadyToPay,
    Settled,
    Adjusted
}

public enum AttemptType
{
    Payment,
    Inquiry,
    StatusCheck,
    Reversal,
    Refund,
    Advice
}

public enum TransportStatus
{
    NotSent,
    Sent,
    Response,
    Timeout,
    ConnectionError
}

public enum ProviderOutcome
{
    Success,
    Failed,
    Pending,
    InDoubt,
    NotSent
}

public enum EndpointType
{
    Merchant,
    Customer,
    BankAccount,
    Wallet,
    Biller,
    Provider,
    VirtualAccount,
    MobileNumber,
    Custom
}

public enum FeeComponentType
{
    MerchantServiceFee,
    ProviderFee,
    Commission,
    Tax,
    RansysMargin,
    Other
}

public sealed record Money(
    decimal Amount,
    string CurrencyCode,
    short CurrencyScale,
    int CurrencyDefinitionVersion);

public sealed record TransactionFingerprint(
    string Value,
    int Version);

public sealed record TransactionIdentity(
    TransactionId RansysTransactionId,
    string ClientReference,
    string? IdempotencyKey,
    TransactionFingerprint Fingerprint,
    TransactionId? OriginalTransactionId);

public sealed record MerchantContext(
    MerchantId MerchantId,
    string MerchantCode,
    ChannelId ChannelId);

public sealed record ProductReference(
    ProductId ProductId,
    string ProductCode,
    string Category);

public sealed record PartyReference(
    string Type,
    string Value);

public sealed record Customer(
    string? CustomerId,
    string? ExternalCustomerReference,
    string? AccountNumber,
    string? PhoneNumber,
    string? Name,
    IReadOnlyDictionary<string, JsonElement> Metadata);

public sealed record TransactionEndpoint(
    EndpointType Type,
    string Identifier,
    string? InstitutionCode,
    string? AccountReference,
    IReadOnlyDictionary<string, JsonElement> Metadata);

public sealed record ExternalReference(
    string Type,
    string Value,
    string Source,
    bool IsPrimary);

public sealed record TransactionReferences(
    string ClientReference,
    string? MerchantReference,
    string? Stan,
    string? Rrn,
    string? ProviderReference,
    string? ProviderStan,
    string? ProviderRrn,
    IReadOnlyList<ExternalReference> ExternalReferences);

public sealed record FeeBeneficiary(
    string Type,
    Guid? Id);

public sealed record FeeComponent(
    FeeComponentType ComponentType,
    Money ChargedAmount,
    Money AccountingAmount,
    FeeBeneficiary Beneficiary,
    bool Refundable,
    long? CalculationRuleVersion);

public sealed record ProviderReference(
    ProviderId ProviderId,
    string ProviderCode,
    string AdapterService);

public sealed record RoutingDecision(
    ProviderReference InitialProvider,
    ProviderReference CurrentProvider,
    long RuleVersion,
    int FailoverCount,
    string? FailoverReason,
    DateTimeOffset DecisionTimestamp);

public sealed record TransactionStatusSnapshot(
    ProcessingStatus Processing,
    FinancialStatus Financial,
    ReconciliationStatus Reconciliation,
    SettlementStatus Settlement,
    string? ResponseCode,
    string? ReasonCode,
    string? ReasonDescription);

public sealed record TransactionTimestamps(
    DateTimeOffset ReceivedAt,
    DateTimeOffset? ValidatedAt,
    DateTimeOffset? ProviderSentAt,
    DateTimeOffset? ProviderResponseAt,
    DateTimeOffset? FinancialPostedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset UpdatedAt);

public sealed record TransactionConfigurationSnapshot(
    Guid? ConfigVersionId,
    Guid? RoutingConfigVersionId,
    Guid? FeeConfigVersionId,
    long? ProviderPolicyVersion,
    int CurrencyDefinitionVersion);

public sealed record CanonicalTransaction(
    TransactionId Id,
    TransactionIdentity Identity,
    TransactionType TransactionType,
    MerchantContext Merchant,
    ProductReference Product,
    Money Amount,
    IReadOnlyList<FeeComponent> Fees,
    Customer? Customer,
    TransactionEndpoint? Source,
    TransactionEndpoint? Destination,
    TransactionReferences References,
    RoutingDecision? Routing,
    TransactionStatusSnapshot Status,
    TransactionTimestamps Timestamps,
    TransactionConfigurationSnapshot Configuration,
    IReadOnlyDictionary<string, JsonElement> Metadata);

public sealed record RawMessageReferences(
    string? RequestUri,
    string? ResponseUri);

public sealed record TransactionAttempt(
    AttemptId AttemptId,
    TransactionId TransactionId,
    int AttemptNumber,
    AttemptType AttemptType,
    ProviderReference Provider,
    bool RequestSent,
    TransportStatus TransportStatus,
    string? ProviderTransactionStatus,
    string? RansysResponseCode,
    string? ProviderResponseCode,
    string? ProviderResponseMessage,
    TransactionReferences References,
    int? LatencyMs,
    RawMessageReferences RawMessages,
    string CorrelationId,
    string TraceId,
    DateTimeOffset? ProviderSentAt,
    DateTimeOffset? ProviderResponseAt,
    DateTimeOffset CreatedAt,
    IReadOnlyDictionary<string, JsonElement> Metadata);

public sealed record CorrelationContext(
    TransactionId TransactionId,
    string CorrelationId,
    string TraceId);

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

public sealed record ProviderCapabilities(
    IReadOnlySet<string> Capabilities);

public sealed record ProviderExecutionContext(
    ProviderReference Provider,
    Guid? EndpointProfileId,
    long? ProductMappingVersion,
    ProviderTimeoutPolicy TimeoutPolicy,
    ProviderConcurrencyPolicy ConcurrencyPolicy,
    ProviderCapabilities Capabilities,
    string? AuthenticationProfileReference,
    long PolicyVersion);

public sealed record ProviderTransactionRequest(
    TransactionId TransactionId,
    AttemptId AttemptId,
    TransactionType TransactionType,
    ProductReference Product,
    Money? Amount,
    Customer? Customer,
    TransactionEndpoint? Source,
    TransactionEndpoint? Destination,
    TransactionReferences References,
    ProviderExecutionContext ProviderContext,
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

public sealed record ProviderRetryHint(
    bool SafeToRetryTransport,
    TimeSpan? SuggestedDelay);

public sealed record ProviderResult(
    ProviderOutcome Outcome,
    ProviderTransportResult Transport,
    string RansysResponseCode,
    string? ProviderResponseCode,
    string? ProviderResponseMessage,
    TransactionReferences References,
    IReadOnlyDictionary<string, JsonElement> Data,
    ProviderRetryHint? RetryHint,
    DateTimeOffset ReceivedAt);

public sealed record AuthenticatedClientContext(
    string ClientId,
    MerchantId MerchantId,
    ChannelId ChannelId,
    string AuthenticationMethod,
    IReadOnlySet<string> Scopes);

public sealed record PaymentCommand(
    AuthenticatedClientContext Client,
    string ClientReference,
    string? IdempotencyKey,
    string ProductCode,
    Money Amount,
    Customer? Customer,
    TransactionEndpoint? Source,
    TransactionEndpoint? Destination,
    DateTimeOffset RequestTimestamp,
    IReadOnlyDictionary<string, JsonElement> Metadata);

public sealed record InquiryCommand(
    AuthenticatedClientContext Client,
    string ClientReference,
    string? IdempotencyKey,
    string ProductCode,
    Customer? Customer,
    TransactionEndpoint? Destination,
    DateTimeOffset RequestTimestamp,
    IReadOnlyDictionary<string, JsonElement> Metadata);

public sealed record RefundCommand(
    AuthenticatedClientContext Client,
    string ClientReference,
    TransactionId OriginalTransactionId,
    Money RefundAmount,
    string Reason,
    DateTimeOffset RequestTimestamp,
    IReadOnlyDictionary<string, JsonElement> Metadata);

public sealed record ReversalCommand(
    AuthenticatedClientContext Client,
    string ClientReference,
    TransactionId OriginalTransactionId,
    string Reason,
    DateTimeOffset RequestTimestamp,
    IReadOnlyDictionary<string, JsonElement> Metadata);

public sealed record TransactionResult(
    TransactionId TransactionId,
    string ClientReference,
    string ResponseCode,
    string ResponseMessage,
    ProcessingStatus TransactionStatus,
    TransactionReferences References,
    IReadOnlyDictionary<string, JsonElement> Data,
    DateTimeOffset Timestamp);

public sealed record EventEnvelope<T>(
    Guid EventId,
    string EventType,
    int EventVersion,
    Guid AggregateId,
    string AggregateType,
    long? SourceVersion,
    DateTimeOffset OccurredAt,
    CorrelationContext Correlation,
    T Data);

public sealed record TransactionSucceededV1(
    TransactionId TransactionId,
    string ClientReference,
    MerchantId MerchantId,
    ProductId ProductId,
    TransactionType TransactionType,
    Money Amount,
    string ResponseCode,
    FinancialStatus FinancialStatus,
    ProviderId ProviderId,
    string? ProviderReference,
    DateTimeOffset FinancialPostedAt);

public sealed record TransactionInDoubtV1(
    TransactionId TransactionId,
    MerchantId MerchantId,
    ProductId ProductId,
    ProviderId ProviderId,
    AttemptId AttemptId,
    string ReasonCode,
    Money ReservedAmount,
    DateTimeOffset OccurredAt,
    long? RecoveryPolicyVersion);
