using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ransys.Api.Contracts.V1;
using Ransys.Api.Errors;
using Ransys.Api.Security;
using Ransys.Domain;
using Ransys.Domain.Transactions;
using Ransys.TransactionCore.Processing;

namespace Ransys.Api.Validation;

/// <summary>
/// Schema-level validation of the OpenAPI v1 request DTOs and their mapping to Transaction Core commands (handoff §9–10).
/// Everything the YAML states is enforced here: required properties, <c>minLength</c>/<c>maxLength</c>, the
/// <c>DecimalAmount</c> and currency patterns, <c>format: uuid</c> / <c>date-time</c> and enum values. Canonical checks
/// that need reference data (product, currency definition and its scale, wallet) stay in Transaction Core. The
/// currency-definition version is never taken from input (OpenAPI v1 §5). Failures are 400 / 2001 with the field path.
/// </summary>
public static partial class RequestValidator
{
    public const int MaxIdempotencyKeyLength = 128;

    /// <summary>OpenAPI <c>EndpointInput.type</c> enum.</summary>
    public static readonly IReadOnlySet<string> EndpointTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "MERCHANT", "CUSTOMER", "BANK_ACCOUNT", "WALLET", "BILLER", "PROVIDER", "VIRTUAL_ACCOUNT", "MOBILE_NUMBER", "CUSTOM",
    };

    /// <summary>OpenAPI <c>DecimalAmount</c> pattern, with <c>\z</c> instead of <c>$</c> so a trailing newline never matches.</summary>
    public const string DecimalAmountRegex = @"^-?(0|[1-9][0-9]*)(\.[0-9]{1,8})?\z";

    /// <summary>OpenAPI <c>MoneyInput.currency</c> pattern (same <c>\z</c> rule).</summary>
    public const string CurrencyRegex = @"^[A-Z]{3}\z";

    [GeneratedRegex(DecimalAmountRegex, RegexOptions.CultureInvariant)]
    private static partial Regex DecimalAmountPattern();

    [GeneratedRegex(CurrencyRegex, RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyPattern();

    // RFC 3339 date-time with a mandatory offset (OpenAPI v1 §13: timezone-aware).
    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex DateTimePattern();

    // Canonical 8-4-4-4-12 hexadecimal UUID.
    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex UuidPattern();

    public static Validated<InquiryCommand> ToCommand(InquiryRequest? request, ClientContext client, string? idempotencyKey)
    {
        var v = new Collector();
        if (request is null)
        {
            return v.Fail<InquiryCommand>("Request body is required.", null);
        }

        var clientReference = v.RequiredText(request.ClientReference, "clientReference", 1, 128);
        var productCode = v.RequiredText(request.ProductCode, "productCode", 1, 64);
        var customer = v.Customer(request.Customer, "customer");
        var destination = v.Endpoint(request.Destination, "destination", required: false);
        var timestamp = v.RequiredTimestamp(request.RequestTimestamp, "requestTimestamp");
        var metadata = Metadata(request.Metadata);
        return v.Result(() => new InquiryCommand(
            client.ChannelId, client.MerchantId, clientReference!, idempotencyKey, productCode!, customer, destination, timestamp!.Value, metadata));
    }

    public static Validated<PaymentCommand> ToCommand(PaymentRequest? request, ClientContext client, string? idempotencyKey)
    {
        var v = new Collector();
        if (request is null)
        {
            return v.Fail<PaymentCommand>("Request body is required.", null);
        }

        var clientReference = v.RequiredText(request.ClientReference, "clientReference", 1, 128);
        var productCode = v.RequiredText(request.ProductCode, "productCode", 1, 64);
        var amount = v.PositiveMoney(request.Amount, "amount");
        var customer = v.Customer(request.Customer, "customer");
        var source = v.Endpoint(request.Source, "source", required: false);
        var destination = v.Endpoint(request.Destination, "destination", required: false);
        var merchantReference = v.OptionalText(request.MerchantReference, "merchantReference", 128);
        var timestamp = v.RequiredTimestamp(request.RequestTimestamp, "requestTimestamp");
        var metadata = Metadata(request.Metadata);
        return v.Result(() => new PaymentCommand(
            client.ChannelId, client.MerchantId, clientReference!, idempotencyKey, productCode!, amount!, customer, source, destination,
            merchantReference, timestamp!.Value, metadata));
    }

    public static Validated<TransferCommand> ToCommand(TransferRequest? request, ClientContext client, string? idempotencyKey)
    {
        var v = new Collector();
        if (request is null)
        {
            return v.Fail<TransferCommand>("Request body is required.", null);
        }

        var clientReference = v.RequiredText(request.ClientReference, "clientReference", 1, 128);
        var productCode = v.RequiredText(request.ProductCode, "productCode", 1, 64);
        var amount = v.PositiveMoney(request.Amount, "amount");
        var source = v.Endpoint(request.Source, "source", required: true);
        var destination = v.Endpoint(request.Destination, "destination", required: true);
        var customer = v.Customer(request.Customer, "customer");
        var timestamp = v.RequiredTimestamp(request.RequestTimestamp, "requestTimestamp");
        var metadata = Metadata(request.Metadata);
        return v.Result(() => new TransferCommand(
            client.ChannelId, client.MerchantId, clientReference!, idempotencyKey, productCode!, amount!, source!, destination!, customer,
            timestamp!.Value, metadata));
    }

    public static Validated<RefundCommand> ToCommand(RefundRequest? request, ClientContext client, string? idempotencyKey)
    {
        var v = new Collector();
        if (request is null)
        {
            return v.Fail<RefundCommand>("Request body is required.", null);
        }

        var clientReference = v.RequiredText(request.ClientReference, "clientReference", 1, 128);
        var original = v.RequiredUuid(request.OriginalTransactionId, "originalTransactionId");
        var amount = v.PositiveMoney(request.RefundAmount, "refundAmount");
        var reason = v.RequiredText(request.Reason, "reason", 1, 500);
        var timestamp = v.RequiredTimestamp(request.RequestTimestamp, "requestTimestamp");
        var metadata = Metadata(request.Metadata);
        return v.Result(() => new RefundCommand(
            client.ChannelId, client.MerchantId, clientReference!, idempotencyKey, original!.Value, amount!, reason!, timestamp!.Value, metadata));
    }

    public static Validated<ReversalCommand> ToCommand(ReversalRequest? request, ClientContext client, string? idempotencyKey)
    {
        var v = new Collector();
        if (request is null)
        {
            return v.Fail<ReversalCommand>("Request body is required.", null);
        }

        var clientReference = v.RequiredText(request.ClientReference, "clientReference", 1, 128);
        var original = v.RequiredUuid(request.OriginalTransactionId, "originalTransactionId");
        var reason = v.RequiredText(request.Reason, "reason", 1, 500);
        var timestamp = v.RequiredTimestamp(request.RequestTimestamp, "requestTimestamp");
        var metadata = Metadata(request.Metadata);
        return v.Result(() => new ReversalCommand(
            client.ChannelId, client.MerchantId, clientReference!, idempotencyKey, original!.Value, reason!, timestamp!.Value, metadata));
    }

    public static Validated<VoidCommand> ToCommand(VoidRequest? request, ClientContext client, string? idempotencyKey)
    {
        var v = new Collector();
        if (request is null)
        {
            return v.Fail<VoidCommand>("Request body is required.", null);
        }

        var clientReference = v.RequiredText(request.ClientReference, "clientReference", 1, 128);
        var original = v.RequiredUuid(request.OriginalTransactionId, "originalTransactionId");
        var reason = v.RequiredText(request.Reason, "reason", 1, 500);
        var timestamp = v.RequiredTimestamp(request.RequestTimestamp, "requestTimestamp");
        var metadata = Metadata(request.Metadata);
        return v.Result(() => new VoidCommand(
            client.ChannelId, client.MerchantId, clientReference!, idempotencyKey, original!.Value, reason!, timestamp!.Value, metadata));
    }

    /// <summary><c>Idempotency-Key</c> header: optional, 1–128 characters when present (never confused with the nonce).</summary>
    public static Validated<string?> IdempotencyKey(string? header)
    {
        if (header is null)
        {
            return new Validated<string?>(null, null);
        }

        return header.Length is 0 or > MaxIdempotencyKeyLength || string.IsNullOrWhiteSpace(header)
            ? new Validated<string?>(null, ApiError.Validation($"Idempotency-Key must be 1 to {MaxIdempotencyKeyLength} characters.", "Idempotency-Key"))
            : new Validated<string?>(header, null);
    }

    /// <summary>Path parameter <c>ransysTransactionId</c> (<c>format: uuid</c>).</summary>
    public static Validated<Guid> TransactionId(string? value) =>
        value is not null && UuidPattern().IsMatch(value) && Guid.TryParse(value, CultureInfo.InvariantCulture, out var id)
            ? new Validated<Guid>(id, null)
            : new Validated<Guid>(Guid.Empty, ApiError.Validation("ransysTransactionId must be a UUID.", "ransysTransactionId"));

    /// <summary>
    /// Parses an OpenAPI <c>DecimalAmount</c> string exactly: pattern first (no exponent, sign other than a leading '-',
    /// whitespace, leading zeros, or more than 8 decimals), then <see cref="decimal.TryParse(string, NumberStyles, IFormatProvider, out decimal)"/>
    /// with the invariant culture. Never float/double.
    /// </summary>
    public static bool TryParseDecimalAmount(string? value, out decimal amount)
    {
        amount = 0m;
        return value is not null
            && DecimalAmountPattern().IsMatch(value)
            && decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);
    }

    private static IReadOnlyDictionary<string, JsonElement>? Metadata(Dictionary<string, JsonElement>? metadata) => metadata;

    /// <summary>First error wins; the order of checks follows the schema's property order.</summary>
    private sealed class Collector
    {
        private ApiError? _error;

        public Validated<T> Fail<T>(string message, string? field)
        {
            _error ??= ApiError.Validation(message, field);
            return new Validated<T>(default!, _error);
        }

        public Validated<T> Result<T>(Func<T> build) => _error is null ? new Validated<T>(build(), null) : new Validated<T>(default!, _error);

        public string? RequiredText(string? value, string field, int min, int max)
        {
            if (value is null)
            {
                Fail<object>($"{field} is required.", field);
                return null;
            }

            return Text(value, field, min, max);
        }

        public string? OptionalText(string? value, string field, int max) => value is null ? null : Text(value, field, 0, max);

        public MoneyInput? PositiveMoney(MoneyInputDto? money, string field)
        {
            if (money is null)
            {
                Fail<object>($"{field} is required.", field);
                return null;
            }

            if (money.Value is null)
            {
                Fail<object>($"{field}.value is required.", $"{field}.value");
                return null;
            }

            if (!TryParseDecimalAmount(money.Value, out var value))
            {
                Fail<object>($"{field}.value must be an exact decimal string with at most 8 decimals.", $"{field}.value");
                return null;
            }

            if (value <= 0m)
            {
                Fail<object>($"{field}.value must be greater than zero.", $"{field}.value");
                return null;
            }

            if (money.Currency is null)
            {
                Fail<object>($"{field}.currency is required.", $"{field}.currency");
                return null;
            }

            if (!CurrencyPattern().IsMatch(money.Currency))
            {
                Fail<object>($"{field}.currency must be a 3-letter uppercase ISO 4217 code.", $"{field}.currency");
                return null;
            }

            return new MoneyInput(value, money.Currency);
        }

        public CustomerInput? Customer(CustomerInputDto? customer, string field)
        {
            if (customer is null)
            {
                return null;
            }

            return new CustomerInput(
                OptionalText(customer.CustomerId, $"{field}.customerId", 128),
                OptionalText(customer.ExternalCustomerReference, $"{field}.externalCustomerReference", 256),
                OptionalText(customer.AccountNumber, $"{field}.accountNumber", 256),
                OptionalText(customer.PhoneNumber, $"{field}.phoneNumber", 64),
                OptionalText(customer.Name, $"{field}.name", 256),
                customer.Metadata);
        }

        public EndpointInput? Endpoint(EndpointInputDto? endpoint, string field, bool required)
        {
            if (endpoint is null)
            {
                if (required)
                {
                    Fail<object>($"{field} is required.", field);
                }

                return null;
            }

            if (endpoint.Type is null)
            {
                Fail<object>($"{field}.type is required.", $"{field}.type");
                return null;
            }

            if (!EndpointTypes.Contains(endpoint.Type) || !CanonicalCodes.EndpointType.TryParse(endpoint.Type, out var type))
            {
                Fail<object>($"{field}.type is not a supported endpoint type.", $"{field}.type");
                return null;
            }

            var identifier = RequiredText(endpoint.Identifier, $"{field}.identifier", 0, 256);
            var institution = OptionalText(endpoint.InstitutionCode, $"{field}.institutionCode", 64);
            var account = OptionalText(endpoint.AccountReference, $"{field}.accountReference", 256);
            return identifier is null ? null : new EndpointInput(type, identifier, institution, account, endpoint.Metadata);
        }

        public DateTimeOffset? RequiredTimestamp(string? value, string field)
        {
            if (value is null)
            {
                Fail<object>($"{field} is required.", field);
                return null;
            }

            if (!DateTimePattern().IsMatch(value)
                || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                Fail<object>($"{field} must be an RFC 3339 date-time with a time zone offset.", field);
                return null;
            }

            return parsed;
        }

        public Guid? RequiredUuid(string? value, string field)
        {
            if (value is null)
            {
                Fail<object>($"{field} is required.", field);
                return null;
            }

            if (!UuidPattern().IsMatch(value) || !Guid.TryParse(value, CultureInfo.InvariantCulture, out var id))
            {
                Fail<object>($"{field} must be a UUID.", field);
                return null;
            }

            return id;
        }

        private string? Text(string value, string field, int min, int max)
        {
            if (value.Length < min || value.Length > max)
            {
                Fail<object>(min > 0 ? $"{field} must be {min} to {max} characters." : $"{field} must be at most {max} characters.", field);
                return null;
            }

            return value;
        }
    }
}

/// <summary>A validated value or the 400 error that rejects the request.</summary>
public readonly record struct Validated<T>(T Value, ApiError? Error)
{
    public bool IsValid => Error is null;
}
