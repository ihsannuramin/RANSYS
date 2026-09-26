using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ransys.Api.Contracts.V1;

// Public request DTOs of RANSYS OpenAPI v1 (docs/RANSYS_OpenAPI_v1.yaml, components.schemas). Property names and required
// sets are checked against the YAML by tests/Ransys.Api.Tests (ContractTests). Every property is nullable so that the
// validator (RequestValidator) can report the exact missing field as a 400 / 2001. JSON with unknown properties is
// rejected by ApiJson.Options (additionalProperties: false). Enum-typed fields are exact strings, validated against the
// OpenAPI enum. These DTOs never reach the domain: RequestValidator maps them to Transaction Core commands.

/// <summary>OpenAPI <c>MoneyInput</c>. <c>value</c> is an exact decimal string (<c>DecimalAmount</c>), never a JSON number.</summary>
public sealed class MoneyInputDto
{
    [SchemaRequired]
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [SchemaRequired]
    [JsonPropertyName("currency")]
    public string? Currency { get; init; }
}

/// <summary>OpenAPI <c>CustomerInput</c>.</summary>
public sealed class CustomerInputDto
{
    [JsonPropertyName("customerId")]
    public string? CustomerId { get; init; }

    [JsonPropertyName("externalCustomerReference")]
    public string? ExternalCustomerReference { get; init; }

    [JsonPropertyName("accountNumber")]
    public string? AccountNumber { get; init; }

    [JsonPropertyName("phoneNumber")]
    public string? PhoneNumber { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}

/// <summary>OpenAPI <c>EndpointInput</c>.</summary>
public sealed class EndpointInputDto
{
    [SchemaRequired]
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [SchemaRequired]
    [JsonPropertyName("identifier")]
    public string? Identifier { get; init; }

    [JsonPropertyName("institutionCode")]
    public string? InstitutionCode { get; init; }

    [JsonPropertyName("accountReference")]
    public string? AccountReference { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}

/// <summary>OpenAPI <c>InquiryRequest</c> (<c>POST /api/v1/inquiries</c>).</summary>
public sealed class InquiryRequest
{
    [SchemaRequired]
    [JsonPropertyName("clientReference")]
    public string? ClientReference { get; init; }

    [SchemaRequired]
    [JsonPropertyName("productCode")]
    public string? ProductCode { get; init; }

    [JsonPropertyName("customer")]
    public CustomerInputDto? Customer { get; init; }

    [JsonPropertyName("destination")]
    public EndpointInputDto? Destination { get; init; }

    [SchemaRequired]
    [JsonPropertyName("requestTimestamp")]
    public string? RequestTimestamp { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}

/// <summary>OpenAPI <c>PaymentRequest</c> (<c>POST /api/v1/payments</c>).</summary>
public sealed class PaymentRequest
{
    [SchemaRequired]
    [JsonPropertyName("clientReference")]
    public string? ClientReference { get; init; }

    [SchemaRequired]
    [JsonPropertyName("productCode")]
    public string? ProductCode { get; init; }

    [SchemaRequired]
    [JsonPropertyName("amount")]
    public MoneyInputDto? Amount { get; init; }

    [JsonPropertyName("customer")]
    public CustomerInputDto? Customer { get; init; }

    [JsonPropertyName("source")]
    public EndpointInputDto? Source { get; init; }

    [JsonPropertyName("destination")]
    public EndpointInputDto? Destination { get; init; }

    [JsonPropertyName("merchantReference")]
    public string? MerchantReference { get; init; }

    [SchemaRequired]
    [JsonPropertyName("requestTimestamp")]
    public string? RequestTimestamp { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}

/// <summary>OpenAPI <c>TransferRequest</c> (<c>POST /api/v1/transfers</c>).</summary>
public sealed class TransferRequest
{
    [SchemaRequired]
    [JsonPropertyName("clientReference")]
    public string? ClientReference { get; init; }

    [SchemaRequired]
    [JsonPropertyName("productCode")]
    public string? ProductCode { get; init; }

    [SchemaRequired]
    [JsonPropertyName("amount")]
    public MoneyInputDto? Amount { get; init; }

    [SchemaRequired]
    [JsonPropertyName("source")]
    public EndpointInputDto? Source { get; init; }

    [SchemaRequired]
    [JsonPropertyName("destination")]
    public EndpointInputDto? Destination { get; init; }

    [JsonPropertyName("customer")]
    public CustomerInputDto? Customer { get; init; }

    [SchemaRequired]
    [JsonPropertyName("requestTimestamp")]
    public string? RequestTimestamp { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}

/// <summary>OpenAPI <c>RefundRequest</c> (<c>POST /api/v1/refunds</c>).</summary>
public sealed class RefundRequest
{
    [SchemaRequired]
    [JsonPropertyName("clientReference")]
    public string? ClientReference { get; init; }

    [SchemaRequired]
    [JsonPropertyName("originalTransactionId")]
    public string? OriginalTransactionId { get; init; }

    [SchemaRequired]
    [JsonPropertyName("refundAmount")]
    public MoneyInputDto? RefundAmount { get; init; }

    [SchemaRequired]
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [SchemaRequired]
    [JsonPropertyName("requestTimestamp")]
    public string? RequestTimestamp { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}

/// <summary>OpenAPI <c>ReversalRequest</c> (<c>POST /api/v1/reversals</c>).</summary>
public sealed class ReversalRequest
{
    [SchemaRequired]
    [JsonPropertyName("clientReference")]
    public string? ClientReference { get; init; }

    [SchemaRequired]
    [JsonPropertyName("originalTransactionId")]
    public string? OriginalTransactionId { get; init; }

    [SchemaRequired]
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [SchemaRequired]
    [JsonPropertyName("requestTimestamp")]
    public string? RequestTimestamp { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}

/// <summary>OpenAPI <c>VoidRequest</c> (<c>POST /api/v1/voids</c>).</summary>
public sealed class VoidRequest
{
    [SchemaRequired]
    [JsonPropertyName("clientReference")]
    public string? ClientReference { get; init; }

    [SchemaRequired]
    [JsonPropertyName("originalTransactionId")]
    public string? OriginalTransactionId { get; init; }

    [SchemaRequired]
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [SchemaRequired]
    [JsonPropertyName("requestTimestamp")]
    public string? RequestTimestamp { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}
