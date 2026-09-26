namespace Ransys.Domain.Attempts;

/// <summary>Adapter-normalized business outcome of a provider response (Canonical Contracts <c>ProviderOutcome</c>).</summary>
public enum ProviderOutcome
{
    Success,
    Failed,
    Pending,
    InDoubt,
    NotSent,
}

/// <summary>What an attempt proves about the transaction.</summary>
public enum AttemptResolutionKind
{
    /// <summary>Proven not delivered: pre-send failover may be considered (State Transition Matrix §15).</summary>
    NotSent,

    /// <summary>Definitive provider success.</summary>
    Success,

    /// <summary>Definitive provider decline / failure.</summary>
    Failed,

    /// <summary>Provider explicitly reports processing in progress.</summary>
    Pending,

    /// <summary>Result cannot be proven: IN_DOUBT, reservation held, no financial failover (PS-08).</summary>
    InDoubt,
}

/// <summary>
/// Maps network truth + adapter outcome to transaction truth. Anything that is not an explicit, consistent
/// provider answer resolves to <see cref="AttemptResolutionKind.InDoubt"/>: TIMEOUT ≠ FAILED.
/// </summary>
public static class AttemptResolution
{
    public static AttemptResolutionKind Classify(AttemptOutcome? outcome, ProviderOutcome? providerOutcome)
    {
        // ADR-005: no recorded outcome (e.g. crash mid-call) is "possibly sent".
        if (outcome is null)
        {
            return AttemptResolutionKind.InDoubt;
        }

        if (outcome.ProvesRequestNotSent)
        {
            return AttemptResolutionKind.NotSent;
        }

        // Sent without a response, read timeout, or connection error after send.
        if (outcome.TransportStatus != TransportStatus.Response)
        {
            return AttemptResolutionKind.InDoubt;
        }

        return providerOutcome switch
        {
            ProviderOutcome.Success => AttemptResolutionKind.Success,
            ProviderOutcome.Failed => AttemptResolutionKind.Failed,
            ProviderOutcome.Pending => AttemptResolutionKind.Pending,

            // A response that the adapter could not map, or that claims "not sent" although a response arrived,
            // proves nothing.
            _ => AttemptResolutionKind.InDoubt,
        };
    }
}
