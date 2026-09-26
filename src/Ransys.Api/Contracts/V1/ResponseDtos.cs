using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ransys.Api.Contracts.V1;

// Public response DTOs of RANSYS OpenAPI v1. They carry only the public contract: no provider identity, routing or fee
// rule versions, posting keys, reservation or ledger ids, row versions, raw-message URIs or secret references
// (handoff §15, OpenAPI v1 §8). Optional properties are omitted when null (ApiJson.Options).

/// <summary>OpenAPI <c>PublicReferences</c>.</summary>
public sealed class PublicReferencesDto
{
    [JsonPropertyName("merchantReference")]
    public string? MerchantReference { get; init; }

    [JsonPropertyName("stan")]
    public string? Stan { get; init; }

    [JsonPropertyName("rrn")]
    public string? Rrn { get; init; }
}

/// <summary>OpenAPI <c>TransactionResponse</c>: result of every POST. <c>responseCode</c> and <c>transactionStatus</c> are independent.</summary>
public sealed class TransactionResponse
{
    [SchemaRequired]
    [JsonPropertyName("ransysTransactionId")]
    public required string RansysTransactionId { get; init; }

    [SchemaRequired]
    [JsonPropertyName("clientReference")]
    public required string ClientReference { get; init; }

    [SchemaRequired]
    [JsonPropertyName("responseCode")]
    public required string ResponseCode { get; init; }

    [SchemaRequired]
    [JsonPropertyName("responseMessage")]
    public required string ResponseMessage { get; init; }

    [SchemaRequired]
    [JsonPropertyName("transactionStatus")]
    public required string TransactionStatus { get; init; }

    [JsonPropertyName("references")]
    public PublicReferencesDto? References { get; init; }

    /// <summary>Always present (an empty object when there is no data).</summary>
    [SchemaRequired]
    [JsonPropertyName("data")]
    public required IReadOnlyDictionary<string, JsonElement> Data { get; init; }

    [SchemaRequired]
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>OpenAPI <c>TransactionDetailResponse</c> (<c>GET /api/v1/transactions/{ransysTransactionId}</c>).</summary>
public sealed class TransactionDetailResponse
{
    [SchemaRequired]
    [JsonPropertyName("ransysTransactionId")]
    public required string RansysTransactionId { get; init; }

    [JsonPropertyName("originalTransactionId")]
    public string? OriginalTransactionId { get; init; }

    [SchemaRequired]
    [JsonPropertyName("clientReference")]
    public required string ClientReference { get; init; }

    [SchemaRequired]
    [JsonPropertyName("transactionType")]
    public required string TransactionType { get; init; }

    [SchemaRequired]
    [JsonPropertyName("processingStatus")]
    public required string ProcessingStatus { get; init; }

    [SchemaRequired]
    [JsonPropertyName("financialStatus")]
    public required string FinancialStatus { get; init; }

    [SchemaRequired]
    [JsonPropertyName("reconciliationStatus")]
    public required string ReconciliationStatus { get; init; }

    [SchemaRequired]
    [JsonPropertyName("settlementStatus")]
    public required string SettlementStatus { get; init; }

    [SchemaRequired]
    [JsonPropertyName("responseCode")]
    public required string ResponseCode { get; init; }

    [JsonPropertyName("responseMessage")]
    public string? ResponseMessage { get; init; }

    [JsonPropertyName("references")]
    public PublicReferencesDto? References { get; init; }

    [SchemaRequired]
    [JsonPropertyName("receivedAt")]
    public required DateTimeOffset ReceivedAt { get; init; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Provider data is not persisted (M12d), so the detail view carries none and the property is omitted.</summary>
    [JsonPropertyName("data")]
    public IReadOnlyDictionary<string, JsonElement>? Data { get; init; }
}

/// <summary>OpenAPI <c>ApiErrorResponse</c> for every non-200 answer (ADR-021).</summary>
public sealed class ApiErrorResponse
{
    [SchemaRequired]
    [JsonPropertyName("errorCode")]
    public required string ErrorCode { get; init; }

    [SchemaRequired]
    [JsonPropertyName("errorMessage")]
    public required string ErrorMessage { get; init; }

    [JsonPropertyName("field")]
    public string? Field { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [SchemaRequired]
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }
}
