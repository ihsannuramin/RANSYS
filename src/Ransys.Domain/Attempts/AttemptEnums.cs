namespace Ransys.Domain.Attempts;

/// <summary>Provider call kinds (Canonical Data Model §43). Attempts are not business transactions.</summary>
public enum AttemptType
{
    Payment,
    Inquiry,
    StatusCheck,
    Reversal,
    Refund,
    Advice,
}

/// <summary>Network truth of one attempt (Canonical Data Model §45). Not a financial status.</summary>
public enum TransportStatus
{
    NotSent,
    Sent,
    Response,
    Timeout,
    ConnectionError,
}
