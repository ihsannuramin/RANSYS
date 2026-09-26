using System.Collections.Immutable;
using Ransys.Domain.Common;
using Ransys.Domain.Monetary;

namespace Ransys.Domain.Ledger;

public sealed record JournalLine(LedgerAccountSpec Account, EntrySide Side, Money Amount);

/// <summary>Who created a posting (<c>created_by_type</c> / <c>created_by_id</c>).</summary>
public sealed record LedgerActor(string Type, Guid? Id)
{
    public static LedgerActor System { get; } = new("SYSTEM", null);

    public static LedgerActor User(Guid userId) => new("USER", userId);
}

public sealed record JournalDraft(
    Guid Id,
    PostingKey PostingKey,
    LedgerOperationType OperationType,
    TransactionId? TransactionId,
    Guid? ReservationId,
    Guid? ApprovalRequestId,
    string BusinessReference,
    string Description,
    DateTimeOffset EffectiveAt,
    DateTimeOffset CreatedAt,
    LedgerActor CreatedBy,
    Guid? CompensatesJournalId,
    IReadOnlyList<JournalLine> Lines);

/// <summary>
/// Immutable, balanced double-entry journal (Ledger Posting Rule Matrix §5, §36): at least two lines, positive
/// amounts, one currency definition, and <c>SUM(debit) = SUM(credit)</c>. The database's deferred balance trigger
/// is only the last line of defense (ERD v1.1 §20).
/// </summary>
public sealed class Journal
{
    /// <summary>DDL v1.1 <c>business_reference varchar(128)</c>.</summary>
    public const int MaxBusinessReferenceLength = 128;

    /// <summary>DDL v1.1 <c>description varchar(500)</c>.</summary>
    public const int MaxDescriptionLength = 500;

    private Journal(JournalDraft draft, ImmutableArray<JournalLine> lines, Money total)
    {
        Id = draft.Id;
        PostingKey = draft.PostingKey;
        OperationType = draft.OperationType;
        TransactionId = draft.TransactionId;
        ReservationId = draft.ReservationId;
        ApprovalRequestId = draft.ApprovalRequestId;
        BusinessReference = draft.BusinessReference;
        Description = draft.Description;
        EffectiveAt = draft.EffectiveAt;
        CreatedAt = draft.CreatedAt;
        CreatedBy = draft.CreatedBy;
        CompensatesJournalId = draft.CompensatesJournalId;
        Lines = lines;
        Total = total;
    }

    public Guid Id { get; }

    public PostingKey PostingKey { get; }

    public LedgerOperationType OperationType { get; }

    public TransactionId? TransactionId { get; }

    public Guid? ReservationId { get; }

    public Guid? ApprovalRequestId { get; }

    public string BusinessReference { get; }

    public string Description { get; }

    public DateTimeOffset EffectiveAt { get; }

    public DateTimeOffset CreatedAt { get; }

    public LedgerActor CreatedBy { get; }

    public Guid? CompensatesJournalId { get; }

    public ImmutableArray<JournalLine> Lines { get; }

    /// <summary>Sum of debit (= sum of credit) amounts.</summary>
    public Money Total { get; }

    public CurrencyDefinition Currency => Total.Currency;

    public static Result<Journal> Create(JournalDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(draft.PostingKey);
        ArgumentNullException.ThrowIfNull(draft.CreatedBy);

        var textError = Text.FirstError(
            Text.Required(draft.BusinessReference, "businessReference", MaxBusinessReferenceLength),
            Text.Required(draft.Description, "description", MaxDescriptionLength),
            Text.Required(draft.CreatedBy.Type, "createdByType", 32));
        if (textError is not null)
        {
            return textError;
        }

        var lines = draft.Lines?.ToImmutableArray() ?? [];
        if (lines.Length < 2)
        {
            return Invalid("A journal needs at least two lines.");
        }

        var currency = lines[0].Amount.Currency;
        if (lines.Any(l => l.Amount.Currency != currency || l.Account.Currency != currency))
        {
            return Invalid("All journal lines must use one currency definition.");
        }

        if (lines.Any(l => l.Amount.IsZero))
        {
            return Invalid("Journal line amounts must be greater than zero.");
        }

        var debit = Money.Sum(lines.Where(l => l.Side == EntrySide.Debit).Select(l => l.Amount), currency);
        var credit = Money.Sum(lines.Where(l => l.Side == EntrySide.Credit).Select(l => l.Amount), currency);
        if (debit.IsFailure || credit.IsFailure)
        {
            return debit.IsFailure ? debit.Error : credit.Error;
        }

        if (debit.Value != credit.Value)
        {
            return new RansysError(
                ErrorCodes.JournalUnbalanced,
                ErrorCategory.Financial,
                $"Journal {draft.PostingKey} is unbalanced: debit {debit.Value}, credit {credit.Value}.");
        }

        return new Journal(draft, lines, debit.Value);
    }

    private static RansysError Invalid(string message) =>
        new(ErrorCodes.JournalInvalid, ErrorCategory.Financial, message);
}
