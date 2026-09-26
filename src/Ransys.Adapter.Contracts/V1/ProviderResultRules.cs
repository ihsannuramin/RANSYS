using Ransys.Domain.Common;

namespace Ransys.Adapter.Contracts.V1;

/// <summary>
/// Consistency rules for a <see cref="ProviderResult"/> (Provider Adapter Contract v1 §9–§11, ADR-005, ADR-018).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Allowed Outcome/Finality pairs are exactly SUCCESS+DEFINITIVE, FAILED+DEFINITIVE, PENDING+NON_FINAL,
/// IN_DOUBT+AMBIGUOUS and NOT_SENT+NOT_APPLICABLE.</item>
/// <item>SUCCESS, FAILED and PENDING are statements of the provider, so they require a provider response:
/// <c>Transport.Status == Response</c> and <c>RequestSent == true</c>.</item>
/// <item>NOT_SENT is the only outcome that permits failover, so it requires <c>RequestSent == false</c> and a
/// transport status that can prove non-delivery (<c>NotSent</c> or <c>ConnectionError</c>, ADR-005).
/// <c>ProtocolError</c> never proves non-delivery (ADR-018).</item>
/// <item>IN_DOUBT means "possibly sent": it requires <c>RequestSent == true</c> and a status other than
/// <c>NotSent</c>. An IN_DOUBT with <c>RequestSent == false</c> would be read as "not sent" by Core.</item>
/// </list>
/// Anything else is impossible and <see cref="Normalize"/> turns it into IN_DOUBT, never into NOT_SENT or FAILED.
/// </remarks>
public static class ProviderResultRules
{
    /// <summary>Reason code (not a response code) returned by <see cref="Validate"/>.</summary>
    public const string InvalidResultCode = "PROVIDER_RESULT_INVALID";

    /// <summary>Error code placed in the transport error of a result rewritten by <see cref="Normalize"/>.</summary>
    public const string NormalizedErrorCode = "PROVIDER_RESULT_NORMALIZED";

    /// <summary>True when <paramref name="result"/> satisfies every rule.</summary>
    public static bool IsValid(ProviderResult result) => Explain(result) is null;

    /// <summary>Checks every rule; the failure message explains the first violated rule.</summary>
    public static Result Validate(ProviderResult result)
    {
        var explanation = Explain(result);
        return explanation is null
            ? Result.Success()
            : Result.Failure(new RansysError(InvalidResultCode, ErrorCategory.Provider, explanation));
    }

    /// <summary>
    /// Returns <paramref name="result"/> unchanged when valid. Otherwise returns a conservative copy:
    /// IN_DOUBT + AMBIGUOUS, <c>RequestSent = true</c>, response code <see cref="ProviderResultCodes.InDoubt"/>,
    /// no retry hint, and the violation recorded in the transport error (unless one is already present).
    /// Raw provider code/message, references and data are kept for investigation.
    /// </summary>
    public static ProviderResult Normalize(ProviderResult result)
    {
        var explanation = Explain(result);
        if (explanation is null)
        {
            return result;
        }

        var transport = result.Transport;
        var status = transport is null || transport.Status == TransportStatus.NotSent || !Enum.IsDefined(transport.Status)
            ? TransportStatus.Sent
            : transport.Status;

        var error = transport?.Error ?? new ProviderError(
            ProviderErrorCategories.Protocol,
            NormalizedErrorCode,
            $"Result normalized to IN_DOUBT: {explanation}",
            RetryableTransportError: false,
            RawCode: null);

        return result with
        {
            Outcome = ProviderOutcome.InDoubt,
            Finality = ResultFinality.Ambiguous,
            Transport = new ProviderTransportResult(
                status,
                RequestSent: true,
                transport?.ConnectDuration,
                transport?.RoundTripDuration,
                error),
            RansysResponseCode = ProviderResultCodes.InDoubt,
            RetryHint = null,
        };
    }

    /// <summary>Null when valid; otherwise the reason the result is impossible.</summary>
    public static string? Explain(ProviderResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var transport = result.Transport;
        if (transport is null)
        {
            return "Transport result is missing.";
        }

        if (!Enum.IsDefined(result.Outcome) || !Enum.IsDefined(result.Finality) || !Enum.IsDefined(transport.Status))
        {
            return "Outcome, finality or transport status is not a defined value.";
        }

        var expectedFinality = result.Outcome switch
        {
            ProviderOutcome.Success => ResultFinality.Definitive,
            ProviderOutcome.Failed => ResultFinality.Definitive,
            ProviderOutcome.Pending => ResultFinality.NonFinal,
            ProviderOutcome.InDoubt => ResultFinality.Ambiguous,
            _ => ResultFinality.NotApplicable,
        };

        if (result.Finality != expectedFinality)
        {
            return $"Outcome {result.Outcome} requires finality {expectedFinality}, not {result.Finality}.";
        }

        switch (result.Outcome)
        {
            case ProviderOutcome.Success:
            case ProviderOutcome.Failed:
            case ProviderOutcome.Pending:
                if (transport.Status != TransportStatus.Response || !transport.RequestSent)
                {
                    return $"Outcome {result.Outcome} requires a provider response (transport Response, RequestSent=true).";
                }

                break;

            case ProviderOutcome.NotSent:
                if (transport.RequestSent)
                {
                    return "Outcome NotSent requires RequestSent=false.";
                }

                if (transport.Status is not (TransportStatus.NotSent or TransportStatus.ConnectionError))
                {
                    return $"Transport status {transport.Status} cannot prove that the request was not sent.";
                }

                break;

            case ProviderOutcome.InDoubt:
                if (!transport.RequestSent || transport.Status == TransportStatus.NotSent)
                {
                    return "Outcome InDoubt means possibly sent: it requires RequestSent=true and a status other than NotSent.";
                }

                break;
        }

        return null;
    }
}
