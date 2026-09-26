using System.Collections.Immutable;
using System.Text.Json;
using Ransys.Adapter.Contracts.V1;
using Ransys.Domain.Attempts;
using Ransys.Domain.Common;
using DomainOutcome = Ransys.Domain.Attempts.ProviderOutcome;
using DomainTransport = Ransys.Domain.Attempts.TransportStatus;
using V1Outcome = Ransys.Adapter.Contracts.V1.ProviderOutcome;
using V1Transport = Ransys.Adapter.Contracts.V1.TransportStatus;

namespace Ransys.TransactionCore.Providers;

/// <summary>What Core concludes from one adapter result.</summary>
/// <param name="Result">The result after <see cref="ProviderResultRules.Normalize"/> (impossible results become IN_DOUBT).</param>
/// <param name="Outcome">The attempt outcome to record (ADR-005).</param>
/// <param name="Resolution">What the result proves about the transaction (<see cref="AttemptResolution.Classify"/>).</param>
/// <param name="ResponseCode">4-digit canonical response code to capture on the transaction, or null.</param>
/// <param name="ReasonCode">Reason code for the state transition.</param>
/// <param name="WasNormalized">True when the adapter result violated the contract rules and was made conservative.</param>
public sealed record InterpretedProviderResult(
    ProviderResult Result,
    AttemptOutcome Outcome,
    AttemptResolutionKind Resolution,
    string? ResponseCode,
    string ReasonCode,
    bool WasNormalized);

/// <summary>
/// Maps a V1 <see cref="ProviderResult"/> to the domain (Provider Adapter Contract v1 §7–§11, ADR-005, ADR-018):
/// <list type="number">
/// <item><see cref="ProviderResultRules.Normalize"/> first: an impossible Outcome/Finality/transport combination is
/// IN_DOUBT with <c>RequestSent = true</c>, never NOT_SENT or FAILED.</item>
/// <item>Transport status maps one to one, including PROTOCOL_ERROR (never proves non-delivery).</item>
/// <item>Fields are fitted to the attempt columns without losing the outcome: blank text becomes null, the response
/// message is truncated, and values that do not fit (over-long references, a malformed RANSYS code, over-long raw URIs)
/// are dropped from their column and kept in the attempt metadata (<c>extension.adapter.*</c>).</item>
/// <item>If the outcome still fails domain validation, a minimal possibly-sent outcome (<c>SENT</c>,
/// <c>RequestSent = true</c>) is recorded and the result is IN_DOUBT. An outcome is never dropped.</item>
/// </list>
/// </summary>
public static class ProviderResultInterpreter
{
    public const string ReasonSuccess = "PROVIDER_SUCCESS";
    public const string ReasonDeclined = "PROVIDER_DECLINED";
    public const string ReasonPending = "PROVIDER_PENDING";
    public const string ReasonInDoubt = "PROVIDER_RESULT_UNKNOWN";
    public const string ReasonNotSent = "PROVIDER_NOT_SENT";
    public const string ReasonInvalidResult = "PROVIDER_RESULT_INVALID";

    /// <summary>Canonical 1002: result unknown / possibly sent (Architecture Spec §21).</summary>
    public const string InDoubtResponseCode = ProviderResultCodes.InDoubt;

    private const string OverflowPrefix = "extension.adapter.";

    public static InterpretedProviderResult Interpret(ProviderResult? result, DateTimeOffset now)
    {
        if (result is null)
        {
            // No result object at all: the call happened, so the request may have been sent.
            return Conservative(
                ProviderResults.InDoubt(
                    V1Transport.Sent,
                    new ProviderError(ProviderErrorCategories.Unknown, ReasonInvalidResult, "The adapter returned no result.", false, null),
                    EmptyReferences(),
                    now),
                wasNormalized: true);
        }

        var normalized = ProviderResultRules.Normalize(result);
        var wasNormalized = !ReferenceEquals(normalized, result);

        var overflow = new List<KeyValuePair<string, JsonElement>>();
        var transport = normalized.Transport;
        var domainTransport = Map(transport.Status);
        var ransysCode = FitResponseCode(normalized.RansysResponseCode, overflow);

        var outcome = AttemptOutcome.Create(
            transport.RequestSent,
            domainTransport,
            providerTransactionStatus: null,
            ransysResponseCode: ransysCode,
            providerResponseCode: Fit(normalized.ProviderResponseCode, AttemptOutcome.MaxProviderResponseCodeLength, "providerResponseCode", overflow),
            providerResponseMessage: Blank(normalized.ProviderResponseMessage),
            providerReference: Fit(normalized.References?.ProviderReference, AttemptOutcome.MaxProviderReferenceLength, "providerReference", overflow),
            providerStan: Fit(normalized.References?.ProviderStan, AttemptOutcome.MaxProviderStanLength, "providerStan", overflow),
            providerRrn: Fit(normalized.References?.ProviderRrn, AttemptOutcome.MaxProviderRrnLength, "providerRrn", overflow),
            latency: transport.RoundTripDuration is { } rtt && rtt >= TimeSpan.Zero ? rtt : null,
            rawMessages: RawReferences(normalized, overflow),
            providerSentAt: null,
            providerResponseAt: domainTransport == DomainTransport.Response ? normalized.ReceivedAt : null,
            metadata: OverflowMetadata(overflow));

        if (outcome.IsFailure)
        {
            return Conservative(normalized, wasNormalized: true);
        }

        var resolution = AttemptResolution.Classify(outcome.Value, Map(normalized.Outcome));
        return new InterpretedProviderResult(
            normalized,
            outcome.Value,
            resolution,
            ResponseCodeFor(resolution, ransysCode),
            ReasonFor(resolution, wasNormalized),
            wasNormalized);
    }

    /// <summary>Minimal outcome that can always be recorded: possibly sent, no provider data.</summary>
    private static InterpretedProviderResult Conservative(ProviderResult result, bool wasNormalized)
    {
        var outcome = AttemptOutcome.Create(requestSent: true, DomainTransport.Sent).Value;
        var inDoubt = result.Outcome == V1Outcome.InDoubt && result.Transport is { RequestSent: true }
            ? result
            : ProviderResultRules.Normalize(result with { Outcome = V1Outcome.InDoubt, Finality = ResultFinality.NotApplicable });
        return new InterpretedProviderResult(
            inDoubt, outcome, AttemptResolutionKind.InDoubt, InDoubtResponseCode, ReasonInvalidResult, wasNormalized);
    }

    private static string? ResponseCodeFor(AttemptResolutionKind resolution, string? adapterCode) => resolution switch
    {
        AttemptResolutionKind.InDoubt => InDoubtResponseCode,
        _ => adapterCode,
    };

    private static string ReasonFor(AttemptResolutionKind resolution, bool wasNormalized) => resolution switch
    {
        AttemptResolutionKind.Success => ReasonSuccess,
        AttemptResolutionKind.Failed => ReasonDeclined,
        AttemptResolutionKind.Pending => ReasonPending,
        AttemptResolutionKind.NotSent => ReasonNotSent,
        _ => wasNormalized ? ReasonInvalidResult : ReasonInDoubt,
    };

    public static DomainTransport Map(V1Transport status) => status switch
    {
        V1Transport.NotSent => DomainTransport.NotSent,
        V1Transport.Sent => DomainTransport.Sent,
        V1Transport.Response => DomainTransport.Response,
        V1Transport.Timeout => DomainTransport.Timeout,
        V1Transport.ConnectionError => DomainTransport.ConnectionError,
        V1Transport.ProtocolError => DomainTransport.ProtocolError,

        // Normalize never lets an undefined value through; if one appears anyway it proves nothing.
        _ => DomainTransport.Sent,
    };

    private static DomainOutcome? Map(V1Outcome outcome) => outcome switch
    {
        V1Outcome.Success => DomainOutcome.Success,
        V1Outcome.Failed => DomainOutcome.Failed,
        V1Outcome.Pending => DomainOutcome.Pending,
        V1Outcome.InDoubt => DomainOutcome.InDoubt,
        V1Outcome.NotSent => DomainOutcome.NotSent,
        _ => null,
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? Fit(string? value, int maxLength, string field, List<KeyValuePair<string, JsonElement>> overflow)
    {
        var text = Blank(value);
        if (text is null || text.Length <= maxLength)
        {
            return text;
        }

        overflow.Add(new(OverflowPrefix + field, JsonSerializer.SerializeToElement(text)));
        return null;
    }

    private static string? FitResponseCode(string? code, List<KeyValuePair<string, JsonElement>> overflow)
    {
        var text = Blank(code);
        if (text is null || (text.Length == 4 && text.All(char.IsAsciiDigit)))
        {
            return text;
        }

        overflow.Add(new(OverflowPrefix + "ransysResponseCode", JsonSerializer.SerializeToElement(text)));
        return null;
    }

    private static RawMessageReferences RawReferences(ProviderResult result, List<KeyValuePair<string, JsonElement>> overflow)
    {
        var request = Fit(result.RawRequestReference, RawMessageReferences.MaxReferenceLength, "rawRequestReference", overflow);
        var response = Fit(result.RawResponseReference, RawMessageReferences.MaxReferenceLength, "rawResponseReference", overflow);
        var references = RawMessageReferences.Create(request, response);
        return references.IsSuccess ? references.Value : RawMessageReferences.None;
    }

    private static ExtensionMetadata OverflowMetadata(List<KeyValuePair<string, JsonElement>> overflow)
    {
        if (overflow.Count == 0)
        {
            return ExtensionMetadata.Empty;
        }

        var metadata = ExtensionMetadata.Create(overflow);
        return metadata.IsSuccess ? metadata.Value : ExtensionMetadata.Empty;
    }

    private static ProviderTransactionReferences EmptyReferences() =>
        new(string.Empty, null, null, null, null, null, null, ImmutableDictionary<string, string>.Empty);
}
