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

    /// <summary>Primary request of a VOID transaction (ADR-019).</summary>
    Void,

    /// <summary>Primary request of a TRANSFER transaction (ADR-017).</summary>
    Transfer,

    /// <summary>Primary request of a BALANCE_INQUIRY transaction.</summary>
    BalanceInquiry,
}

/// <summary>Network truth of one attempt (Canonical Data Model §45). Not a financial status.</summary>
public enum TransportStatus
{
    NotSent,
    Sent,
    Response,
    Timeout,
    ConnectionError,

    /// <summary>
    /// The adapter could not interpret the provider's reply (malformed or unmappable). ADR-018: may occur before or
    /// after send; it never proves non-delivery, so a sent or possibly sent request stays IN_DOUBT.
    /// </summary>
    ProtocolError,
}
