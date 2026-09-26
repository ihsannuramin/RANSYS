using System.Globalization;
using Ransys.Domain.Common;

namespace Ransys.Domain.Monetary;

/// <summary>
/// Exact, currency-aware monetary amount (Canonical Data Model §12–14, Architecture Spec §23).
/// <list type="bullet">
/// <item>Always <see cref="decimal"/>; never float/double.</item>
/// <item>Fractional precision may not exceed the currency definition scale.</item>
/// <item>Non-negative: every monetary column in DDL v1.1 is <c>&gt;= 0</c>; direction is expressed by
/// ledger debit/credit sides, not by negative amounts.</item>
/// <item>Must fit PostgreSQL <c>NUMERIC(30,8)</c> (ERD v1.1 §4).</item>
/// <item>No operators or implicit conversions; arithmetic requires the same currency definition.</item>
/// </list>
/// Equality includes the currency definition, not the amount only.
/// </summary>
public sealed record Money
{
    /// <summary>NUMERIC(30,8) allows at most 22 integer digits.</summary>
    public static readonly decimal ExclusiveUpperBound = 10_000_000_000_000_000_000_000m;

    private Money(decimal amount, CurrencyDefinition currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public CurrencyDefinition Currency { get; }

    public string CurrencyCode => Currency.Code;

    public short CurrencyScale => Currency.Scale;

    public int CurrencyDefinitionVersion => Currency.Version;

    public bool IsZero => Amount == 0m;

    public static Result<Money> Create(decimal amount, CurrencyDefinition currency)
    {
        ArgumentNullException.ThrowIfNull(currency);

        if (amount < 0m)
        {
            return RansysError.Validation(ErrorCodes.MoneyNegative, "Amount must not be negative.", "amount");
        }

        if (amount >= ExclusiveUpperBound)
        {
            return RansysError.Validation(ErrorCodes.MoneyOutOfRange, "Amount exceeds NUMERIC(30,8) range.", "amount");
        }

        if (decimal.Round(amount, currency.Scale) != amount)
        {
            return RansysError.Validation(
                ErrorCodes.MoneyPrecisionExceedsScale,
                $"Amount has more than {currency.Scale} fractional digits allowed by {currency}.",
                "amount");
        }

        return new Money(amount, currency);
    }

    public static Money Zero(CurrencyDefinition currency) =>
        new(0m, currency ?? throw new ArgumentNullException(nameof(currency)));

    /// <summary>Sums amounts that must all share <paramref name="currency"/>. An empty sequence yields zero.</summary>
    public static Result<Money> Sum(IEnumerable<Money> values, CurrencyDefinition currency)
    {
        ArgumentNullException.ThrowIfNull(values);

        var total = Zero(currency);
        foreach (var value in values)
        {
            var next = total.Add(value);
            if (next.IsFailure)
            {
                return next;
            }

            total = next.Value;
        }

        return total;
    }

    public bool HasSameCurrency(Money other) =>
        Currency == (other ?? throw new ArgumentNullException(nameof(other))).Currency;

    public Result<Money> Add(Money other)
    {
        EnsureSameCurrency(other);
        return Create(Amount + other.Amount, Currency);
    }

    /// <summary>Fails with <see cref="ErrorCodes.MoneyNegative"/> when the result would be negative.</summary>
    public Result<Money> Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return Create(Amount - other.Amount, Currency);
    }

    public int CompareTo(Money other)
    {
        EnsureSameCurrency(other);
        return Amount.CompareTo(other.Amount);
    }

    public bool IsGreaterThan(Money other) => CompareTo(other) > 0;

    public bool IsGreaterThanOrEqualTo(Money other) => CompareTo(other) >= 0;

    public bool IsLessThan(Money other) => CompareTo(other) < 0;

    /// <summary>
    /// Culture-invariant decimal string padded to the currency scale, e.g. <c>100000.00</c>.
    /// Used for fingerprints and exact serialization.
    /// </summary>
    public string ToCanonicalAmountString() =>
        Amount.ToString("F" + Currency.Scale.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    public override string ToString() => $"{ToCanonicalAmountString()} {Currency}";

    private void EnsureSameCurrency(Money other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Currency != other.Currency)
        {
            throw new CurrencyMismatchException(Currency, other.Currency);
        }
    }
}
