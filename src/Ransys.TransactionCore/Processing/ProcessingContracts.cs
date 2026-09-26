using System.Text.Json;
using Ransys.Domain;
using Ransys.Domain.Transactions;

namespace Ransys.TransactionCore.Processing;

// Application-level commands, one per OpenAPI v1 endpoint. Field names follow the OpenAPI request schemas; the API layer
// parses JSON/decimal strings and authenticates the client, then passes the authenticated ChannelId / MerchantId here.
// These are not API DTOs: no JSON attributes, no HTTP concerns.

/// <summary>OpenAPI <c>MoneyInput</c>: exact decimal value and ISO currency code; the definition version is resolved by Core.</summary>
public sealed record MoneyInput(decimal Value, string Currency);

/// <summary>OpenAPI <c>CustomerInput</c>.</summary>
public sealed record CustomerInput(
    string? CustomerId = null,
    string? ExternalCustomerReference = null,
    string? AccountNumber = null,
    string? PhoneNumber = null,
    string? Name = null,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null);

/// <summary>OpenAPI <c>EndpointInput</c>.</summary>
public sealed record EndpointInput(
    EndpointType Type,
    string Identifier,
    string? InstitutionCode = null,
    string? AccountReference = null,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null);

/// <summary><c>POST /api/v1/inquiries</c>.</summary>
public sealed record InquiryCommand(
    ChannelId ChannelId,
    MerchantId MerchantId,
    string ClientReference,
    string? IdempotencyKey,
    string ProductCode,
    CustomerInput? Customer,
    EndpointInput? Destination,
    DateTimeOffset RequestTimestamp,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null);

/// <summary><c>POST /api/v1/payments</c>.</summary>
public sealed record PaymentCommand(
    ChannelId ChannelId,
    MerchantId MerchantId,
    string ClientReference,
    string? IdempotencyKey,
    string ProductCode,
    MoneyInput Amount,
    CustomerInput? Customer,
    EndpointInput? Source,
    EndpointInput? Destination,
    string? MerchantReference,
    DateTimeOffset RequestTimestamp,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null);

/// <summary><c>POST /api/v1/transfers</c>.</summary>
public sealed record TransferCommand(
    ChannelId ChannelId,
    MerchantId MerchantId,
    string ClientReference,
    string? IdempotencyKey,
    string ProductCode,
    MoneyInput Amount,
    EndpointInput Source,
    EndpointInput Destination,
    CustomerInput? Customer,
    DateTimeOffset RequestTimestamp,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null);

/// <summary><c>POST /api/v1/refunds</c>.</summary>
public sealed record RefundCommand(
    ChannelId ChannelId,
    MerchantId MerchantId,
    string ClientReference,
    string? IdempotencyKey,
    Guid OriginalTransactionId,
    MoneyInput RefundAmount,
    string Reason,
    DateTimeOffset RequestTimestamp,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null);

/// <summary><c>POST /api/v1/reversals</c>.</summary>
public sealed record ReversalCommand(
    ChannelId ChannelId,
    MerchantId MerchantId,
    string ClientReference,
    string? IdempotencyKey,
    Guid OriginalTransactionId,
    string Reason,
    DateTimeOffset RequestTimestamp,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null);

/// <summary><c>POST /api/v1/voids</c>.</summary>
public sealed record VoidCommand(
    ChannelId ChannelId,
    MerchantId MerchantId,
    string ClientReference,
    string? IdempotencyKey,
    Guid OriginalTransactionId,
    string Reason,
    DateTimeOffset RequestTimestamp,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null);

/// <summary>OpenAPI <c>PublicReferences</c>.</summary>
public sealed record PublicReferences(string? MerchantReference, string? Stan, string? Rrn);

/// <summary>
/// Application result of one request (maps to OpenAPI <c>TransactionResponse</c>). <see cref="ProcessingStatus"/> is the
/// public <c>transactionStatus</c>; <see cref="ResponseCode"/> is a separate concept (OpenAPI v1 §6–7).
/// </summary>
/// <param name="IsReplay">True when an idempotent retry returned an existing transaction (no provider call was made).</param>
/// <param name="Data">Adapter data of this call's provider result; empty on a replay (provider data is not persisted).</param>
public sealed record TransactionProcessingResult(
    TransactionId TransactionId,
    string ClientReference,
    TransactionType TransactionType,
    ProcessingStatus ProcessingStatus,
    string ResponseCode,
    string ResponseMessage,
    PublicReferences References,
    IReadOnlyDictionary<string, JsonElement> Data,
    DateTimeOffset Timestamp,
    bool IsReplay);

/// <summary>Options of <see cref="TransactionProcessingService"/>.</summary>
/// <param name="NonMonetaryCurrencyCode">
/// Currency of the explicit zero amount of an inquiry (the DDL requires an amount and currency on every transaction).
/// TODO / Architecture Decision Required: per-product default currency (Configuration Schema v1).
/// </param>
/// <param name="MaxProviderAttempts">Bound on pre-send failover: providers tried per transaction.</param>
public sealed record TransactionProcessingOptions(string NonMonetaryCurrencyCode = "IDR", int MaxProviderAttempts = 3)
{
    public static TransactionProcessingOptions Default { get; } = new();
}

/// <summary>Error codes returned by <see cref="TransactionProcessingService"/> before a transaction is created.</summary>
public static class ProcessingErrorCodes
{
    /// <summary>Unknown or inactive product code (400 / 2001).</summary>
    public const string ProductNotAvailable = "PRODUCT_NOT_AVAILABLE";

    /// <summary>No ACTIVE currency definition for the code (400 / 2001).</summary>
    public const string CurrencyNotSupported = "CURRENCY_NOT_SUPPORTED";

    /// <summary>ADR-021: unknown original, or not visible to the authenticated channel (400 / 2001, field originalTransactionId).</summary>
    public const string OriginalTransactionInvalid = "ORIGINAL_TRANSACTION_INVALID";
}

/// <summary>
/// Canonical response codes used by processing. Only the codes approved in Architecture Spec §21 / PRD are used.
/// TODO / Architecture Decision Required: RANSYS Response Code Catalog v1 (e.g. a code for a frozen wallet, provider
/// declines, pending). Until then unmapped failures fall back to 1001 and non-final states to 1002.
/// </summary>
public static class RansysResponseCodes
{
    public const string Success = "0000";
    public const string InternalError = "1001";
    public const string InDoubt = "1002";
    public const string InvalidRequest = "2001";
    public const string DuplicateReferenceConflict = "2003";
    public const string InvalidSignature = "3001";
    public const string InsufficientBalance = "4001";
    public const string NoRouteAvailable = "5001";

    /// <summary>Response code shown for a transaction: 0000 on SUCCESS, 1002 on IN_DOUBT, otherwise the captured code.</summary>
    public static string ForTransaction(ProcessingStatus status, string? capturedCode) => status switch
    {
        ProcessingStatus.Success => Success,
        ProcessingStatus.InDoubt => InDoubt,
        ProcessingStatus.Failed => IsCanonical(capturedCode) && capturedCode != Success ? capturedCode! : InternalError,
        ProcessingStatus.Reversed or ProcessingStatus.PartiallyRefunded or ProcessingStatus.Refunded =>
            IsCanonical(capturedCode) ? capturedCode! : Success,
        _ => IsCanonical(capturedCode) && capturedCode != Success ? capturedCode! : InDoubt,
    };

    public static string MessageFor(string code, ProcessingStatus status) => (code, status) switch
    {
        (_, ProcessingStatus.Pending) => "Transaction pending",
        (_, ProcessingStatus.Received or ProcessingStatus.Validated or ProcessingStatus.Processing) => "Transaction processing",
        (Success, _) => "Success",
        (InternalError, _) => "Transaction failed",
        (InDoubt, _) => "Transaction response timeout",
        (InvalidRequest, _) => "Invalid request",
        (DuplicateReferenceConflict, _) => "Duplicate reference conflict",
        (InvalidSignature, _) => "Invalid signature",
        (InsufficientBalance, _) => "Insufficient balance",
        (NoRouteAvailable, _) => "No route available",
        _ => status == ProcessingStatus.Failed ? "Transaction failed" : "Transaction processed",
    };

    private static bool IsCanonical(string? code) => code is { Length: 4 } && code.All(char.IsAsciiDigit);
}
