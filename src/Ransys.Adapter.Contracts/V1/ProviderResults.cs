using System.Text.Json;

namespace Ransys.Adapter.Contracts.V1;

/// <summary>
/// Factories for results produced without a provider response. Every result they build satisfies
/// <see cref="ProviderResultRules"/>.
/// </summary>
public static class ProviderResults
{
    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyData = new Dictionary<string, JsonElement>();

    /// <summary>
    /// Normalized capability failure for an operation the adapter does not support (§3): NOT_SENT + NOT_APPLICABLE,
    /// <c>RequestSent = false</c>, error category CAPABILITY_UNSUPPORTED. The response code is the interim
    /// <see cref="ProviderResultCodes.CapabilityUnsupported"/> (TODO: Response Code Catalog v1).
    /// </summary>
    public static ProviderResult CapabilityUnsupported(
        string capabilityCode,
        ProviderTransactionReferences references,
        DateTimeOffset receivedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityCode);

        return NotSent(
            TransportStatus.NotSent,
            new ProviderError(
                ProviderErrorCategories.CapabilityUnsupported,
                ProviderErrorCategories.CapabilityUnsupported,
                $"Capability {capabilityCode} is not supported by this adapter.",
                RetryableTransportError: false,
                RawCode: null),
            ProviderResultCodes.CapabilityUnsupported,
            references,
            receivedAt);
    }

    /// <summary>
    /// NOT_SENT + NOT_APPLICABLE with <c>RequestSent = false</c>. Use only when non-delivery is proven;
    /// <paramref name="status"/> must be <see cref="TransportStatus.NotSent"/> or <see cref="TransportStatus.ConnectionError"/>.
    /// </summary>
    public static ProviderResult NotSent(
        TransportStatus status,
        ProviderError error,
        string ransysResponseCode,
        ProviderTransactionReferences references,
        DateTimeOffset receivedAt)
    {
        if (status is not (TransportStatus.NotSent or TransportStatus.ConnectionError))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Only NotSent or ConnectionError can prove non-delivery.");
        }

        ArgumentNullException.ThrowIfNull(error);
        ArgumentException.ThrowIfNullOrWhiteSpace(ransysResponseCode);
        ArgumentNullException.ThrowIfNull(references);

        return new ProviderResult(
            ProviderOutcome.NotSent,
            ResultFinality.NotApplicable,
            new ProviderTransportResult(status, RequestSent: false, ConnectDuration: null, RoundTripDuration: null, error),
            ransysResponseCode,
            ProviderResponseCode: null,
            ProviderResponseMessage: null,
            references,
            EmptyData,
            RetryHint: null,
            receivedAt,
            RawRequestReference: null,
            RawResponseReference: null);
    }

    /// <summary>
    /// IN_DOUBT + AMBIGUOUS with <c>RequestSent = true</c> (possibly sent) and response code
    /// <see cref="ProviderResultCodes.InDoubt"/>. <paramref name="status"/> must not be <see cref="TransportStatus.NotSent"/>.
    /// </summary>
    public static ProviderResult InDoubt(
        TransportStatus status,
        ProviderError error,
        ProviderTransactionReferences references,
        DateTimeOffset receivedAt,
        TimeSpan? roundTripDuration = null)
    {
        if (status == TransportStatus.NotSent || !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "IN_DOUBT means possibly sent.");
        }

        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(references);

        return new ProviderResult(
            ProviderOutcome.InDoubt,
            ResultFinality.Ambiguous,
            new ProviderTransportResult(status, RequestSent: true, ConnectDuration: null, roundTripDuration, error),
            ProviderResultCodes.InDoubt,
            ProviderResponseCode: null,
            ProviderResponseMessage: null,
            references,
            EmptyData,
            RetryHint: null,
            receivedAt,
            RawRequestReference: null,
            RawResponseReference: null);
    }
}
