using Ransys.Domain.Common;

namespace Ransys.Domain.Attempts;

/// <summary>
/// Normalized result of one provider call as reported by the adapter and persisted by Transaction Core
/// (ADR-005). Transport consistency rules:
/// <list type="bullet">
/// <item><c>NOT_SENT</c> requires <c>RequestSent = false</c>.</item>
/// <item><c>SENT</c> and <c>RESPONSE</c> require <c>RequestSent = true</c>.</item>
/// <item><c>TIMEOUT</c> / <c>CONNECTION_ERROR</c> / <c>PROTOCOL_ERROR</c> (ADR-018) may carry either value; only
/// <c>NOT_SENT</c> or <c>CONNECTION_ERROR</c> with <c>RequestSent = false</c> proves non-delivery
/// (see <see cref="ProvesRequestNotSent"/>).</item>
/// </list>
/// </summary>
public sealed record AttemptOutcome
{
    public const int MaxProviderResponseCodeLength = 64;
    public const int MaxProviderResponseMessageLength = 500;
    public const int MaxProviderTransactionStatusLength = 32;
    public const int MaxProviderReferenceLength = 128;
    public const int MaxProviderStanLength = 32;
    public const int MaxProviderRrnLength = 64;

    private AttemptOutcome(
        bool requestSent,
        TransportStatus transportStatus,
        string? providerTransactionStatus,
        string? ransysResponseCode,
        string? providerResponseCode,
        string? providerResponseMessage,
        string? providerReference,
        string? providerStan,
        string? providerRrn,
        TimeSpan? latency,
        RawMessageReferences rawMessages,
        DateTimeOffset? providerSentAt,
        DateTimeOffset? providerResponseAt,
        ExtensionMetadata metadata)
    {
        RequestSent = requestSent;
        TransportStatus = transportStatus;
        ProviderTransactionStatus = providerTransactionStatus;
        RansysResponseCode = ransysResponseCode;
        ProviderResponseCode = providerResponseCode;
        ProviderResponseMessage = providerResponseMessage;
        ProviderReference = providerReference;
        ProviderStan = providerStan;
        ProviderRrn = providerRrn;
        Latency = latency;
        RawMessages = rawMessages;
        ProviderSentAt = providerSentAt;
        ProviderResponseAt = providerResponseAt;
        Metadata = metadata;
    }

    /// <summary>Financially significant (Canonical Data Model §44).</summary>
    public bool RequestSent { get; }

    public TransportStatus TransportStatus { get; }

    public string? ProviderTransactionStatus { get; }

    /// <summary>4-digit canonical response code (Architecture Spec §21).</summary>
    public string? RansysResponseCode { get; }

    public string? ProviderResponseCode { get; }

    public string? ProviderResponseMessage { get; }

    public string? ProviderReference { get; }

    public string? ProviderStan { get; }

    public string? ProviderRrn { get; }

    public TimeSpan? Latency { get; }

    public RawMessageReferences RawMessages { get; }

    public DateTimeOffset? ProviderSentAt { get; }

    public DateTimeOffset? ProviderResponseAt { get; }

    public ExtensionMetadata Metadata { get; }

    /// <summary>
    /// True only when the adapter explicitly proved the financial request never left RANSYS.
    /// This is the sole condition under which pre-send failover may be considered (ADR-005).
    /// </summary>
    public bool ProvesRequestNotSent =>
        !RequestSent && TransportStatus is TransportStatus.NotSent or TransportStatus.ConnectionError;

    public static Result<AttemptOutcome> Create(
        bool requestSent,
        TransportStatus transportStatus,
        string? providerTransactionStatus = null,
        string? ransysResponseCode = null,
        string? providerResponseCode = null,
        string? providerResponseMessage = null,
        string? providerReference = null,
        string? providerStan = null,
        string? providerRrn = null,
        TimeSpan? latency = null,
        RawMessageReferences? rawMessages = null,
        DateTimeOffset? providerSentAt = null,
        DateTimeOffset? providerResponseAt = null,
        ExtensionMetadata? metadata = null)
    {
        if (!Enum.IsDefined(transportStatus))
        {
            return RansysError.Validation(ErrorCodes.OutOfRange, "Unknown transport status.", "transportStatus");
        }

        var transportError = ValidateTransport(requestSent, transportStatus, providerSentAt);
        if (transportError is not null)
        {
            return transportError;
        }

        var error = Text.FirstError(
            Text.Optional(providerTransactionStatus, "providerTransactionStatus", MaxProviderTransactionStatusLength),
            Text.Optional(providerResponseCode, "providerResponseCode", MaxProviderResponseCodeLength),
            Text.Optional(providerReference, "providerReference", MaxProviderReferenceLength),
            Text.Optional(providerStan, "providerStan", MaxProviderStanLength),
            Text.Optional(providerRrn, "providerRrn", MaxProviderRrnLength));
        if (error is not null)
        {
            return error;
        }

        if (ransysResponseCode is not null && (ransysResponseCode.Length != 4 || !ransysResponseCode.All(char.IsAsciiDigit)))
        {
            return RansysError.Validation(ErrorCodes.InvalidFormat, "RANSYS response code must be 4 digits.", "ransysResponseCode");
        }

        if (latency < TimeSpan.Zero)
        {
            return RansysError.Validation(ErrorCodes.OutOfRange, "Latency cannot be negative.", "latency");
        }

        if (providerSentAt is not null && providerResponseAt < providerSentAt)
        {
            return RansysError.Validation(
                ErrorCodes.OutOfRange, "Provider response cannot precede the request send time.", "providerResponseAt");
        }

        return new AttemptOutcome(
            requestSent,
            transportStatus,
            providerTransactionStatus,
            ransysResponseCode,
            providerResponseCode,
            TruncateDisplayText(providerResponseMessage),
            providerReference,
            providerStan,
            providerRrn,
            latency,
            rawMessages ?? RawMessageReferences.None,
            providerSentAt,
            providerResponseAt,
            metadata ?? ExtensionMetadata.Empty);
    }

    private static RansysError? ValidateTransport(bool requestSent, TransportStatus status, DateTimeOffset? providerSentAt)
    {
        var consistent = status switch
        {
            TransportStatus.NotSent => !requestSent,
            TransportStatus.Sent or TransportStatus.Response => requestSent,
            _ => true,
        };

        if (!consistent)
        {
            return RansysError.Validation(
                ErrorCodes.AttemptInconsistentTransport,
                $"Transport status {CanonicalCodes.TransportStatus.ToCode(status)} is inconsistent with requestSent={requestSent}.",
                "requestSent");
        }

        if (!requestSent && providerSentAt is not null)
        {
            return RansysError.Validation(
                ErrorCodes.AttemptInconsistentTransport, "providerSentAt must be absent when the request was not sent.", "providerSentAt");
        }

        return null;
    }

    // Provider free text is display-only; an over-long message must not prevent recording the outcome.
    private static string? TruncateDisplayText(string? message) =>
        message is { Length: > MaxProviderResponseMessageLength } ? message[..MaxProviderResponseMessageLength] : message;
}
