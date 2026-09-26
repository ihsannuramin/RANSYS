using Ransys.Domain.Common;

namespace Ransys.Domain.Transactions;

/// <summary>
/// Identity of a business transaction (Canonical Data Model §7–8).
/// ClientReference, IdempotencyKey, Fingerprint and the security nonce are distinct concepts.
/// </summary>
public sealed record TransactionIdentity
{
    /// <summary>Canonical max length (Canonical Data Model §7, DDL <c>varchar(128)</c>).</summary>
    public const int MaxClientReferenceLength = 128;

    public const int MaxIdempotencyKeyLength = 128;

    private TransactionIdentity(
        TransactionId ransysTransactionId,
        string clientReference,
        string? idempotencyKey,
        TransactionFingerprint fingerprint,
        TransactionId? originalTransactionId)
    {
        RansysTransactionId = ransysTransactionId;
        ClientReference = clientReference;
        IdempotencyKey = idempotencyKey;
        Fingerprint = fingerprint;
        OriginalTransactionId = originalTransactionId;
    }

    public TransactionId RansysTransactionId { get; }

    /// <summary>Business reference supplied by the client; idempotency scope is channel + client reference.</summary>
    public string ClientReference { get; }

    /// <summary>Optional explicit API retry identity; not equivalent to <see cref="ClientReference"/>.</summary>
    public string? IdempotencyKey { get; }

    public TransactionFingerprint Fingerprint { get; }

    /// <summary>Set for child transactions such as REFUND or REVERSAL.</summary>
    public TransactionId? OriginalTransactionId { get; }

    public static Result<TransactionIdentity> Create(
        TransactionId ransysTransactionId,
        string? clientReference,
        string? idempotencyKey,
        TransactionFingerprint fingerprint,
        TransactionId? originalTransactionId = null)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);

        var error = Text.FirstError(
            Text.Required(clientReference, "clientReference", MaxClientReferenceLength),
            Text.Optional(idempotencyKey, "idempotencyKey", MaxIdempotencyKeyLength));
        if (error is not null)
        {
            return error;
        }

        if (originalTransactionId == ransysTransactionId)
        {
            return RansysError.Validation(
                ErrorCodes.OriginalTransactionSelfReference,
                "A transaction cannot reference itself as its original transaction.",
                "originalTransactionId");
        }

        return new TransactionIdentity(
            ransysTransactionId, clientReference!, idempotencyKey, fingerprint, originalTransactionId);
    }
}
