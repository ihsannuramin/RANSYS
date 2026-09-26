using Ransys.Domain.Common;

namespace Ransys.Domain.Ledger;

/// <summary>
/// Idempotency identity of one ledger posting (Ledger Posting Rule Matrix §6). One business event can never
/// produce a second posting with the same key. The format is fixed by ADR-001 and built only here.
/// </summary>
public sealed record PostingKey
{
    /// <summary>DDL v1.1 <c>posting_key varchar(200)</c>.</summary>
    public const int MaxLength = 200;

    private PostingKey(string value) => Value = value;

    public string Value { get; }

    public static PostingKey Reserve(TransactionId transactionId) => new($"TX:{transactionId}:RESERVE");

    public static PostingKey Post(TransactionId transactionId) => new($"TX:{transactionId}:POST");

    public static PostingKey Release(TransactionId transactionId) => new($"TX:{transactionId}:RELEASE");

    /// <summary>OP-08: reversal while the reservation is still active.</summary>
    public static PostingKey ReversalRelease(TransactionId transactionId) => new($"TX:{transactionId}:REVERSAL_RELEASE");

    /// <summary>OP-09: compensating reversal of a posted payment.</summary>
    public static Result<PostingKey> Reversal(TransactionId transactionId, string? reversalReference) =>
        WithReference($"TX:{transactionId}:REVERSAL:", reversalReference, "reversalReference");

    /// <summary>Refund of the original transaction <paramref name="originalTransactionId"/>.</summary>
    public static Result<PostingKey> Refund(TransactionId originalTransactionId, string? refundReference) =>
        WithReference($"TX:{originalTransactionId}:REFUND:", refundReference, "refundReference");

    /// <summary>Prefix shared by every refund posting of one original transaction.</summary>
    public static string RefundPrefix(TransactionId originalTransactionId) => $"TX:{originalTransactionId}:REFUND:";

    public static Result<PostingKey> TopUp(string? topUpReference) =>
        WithReference("TOPUP:", topUpReference, "topUpReference");

    public static Result<PostingKey> Adjustment(string? adjustmentReference) =>
        WithReference("ADJUSTMENT:", adjustmentReference, "adjustmentReference");

    public override string ToString() => Value;

    private static Result<PostingKey> WithReference(string prefix, string? reference, string field)
    {
        var error = Text.Required(reference, field, MaxLength - prefix.Length);
        return error is null ? new PostingKey(prefix + reference) : error;
    }
}
