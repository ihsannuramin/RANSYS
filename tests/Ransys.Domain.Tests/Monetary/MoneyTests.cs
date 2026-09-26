using Ransys.Domain.Common;
using Ransys.Domain.Monetary;
using static Ransys.Domain.Tests.TestData;

namespace Ransys.Domain.Tests.Monetary;

public sealed class CurrencyDefinitionTests
{
    [Theory]
    [InlineData("idr", "IDR")]
    [InlineData("Usd", "USD")]
    public void Code_is_normalized_to_uppercase(string input, string expected)
    {
        Assert.Equal(expected, CurrencyDefinition.Create(input, 1, 2).Value.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ID")]
    [InlineData("IDRR")]
    [InlineData("1DR")]
    [InlineData("ID ")]
    public void Invalid_code_is_rejected(string? code)
    {
        var result = CurrencyDefinition.Create(code, 1, 2);

        Assert.Equal(ErrorCodes.CurrencyCodeInvalid, result.Error.Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(9)]
    public void Scale_outside_0_to_8_is_rejected(short scale)
    {
        Assert.Equal(ErrorCodes.CurrencyScaleInvalid, CurrencyDefinition.Create("IDR", 1, scale).Error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Non_positive_version_is_rejected(int version)
    {
        Assert.Equal(ErrorCodes.CurrencyVersionInvalid, CurrencyDefinition.Create("IDR", version, 2).Error.Code);
    }

    [Fact]
    public void Definitions_with_same_code_but_different_version_are_different()
    {
        Assert.NotEqual(Idr, IdrV2);
    }
}

public sealed class MoneyTests
{
    [Fact]
    public void Creates_money_within_currency_scale()
    {
        var money = Money.Create(102_500.50m, Idr).Value;

        Assert.Equal(102_500.50m, money.Amount);
        Assert.Equal("IDR", money.CurrencyCode);
        Assert.Equal(2, money.CurrencyScale);
        Assert.Equal(1, money.CurrencyDefinitionVersion);
    }

    [Theory]
    [InlineData(0.001)]
    [InlineData(10.125)]
    public void Precision_beyond_scale_is_rejected(double raw)
    {
        var result = Money.Create((decimal)raw, Idr);

        Assert.Equal(ErrorCodes.MoneyPrecisionExceedsScale, result.Error.Code);
    }

    [Fact]
    public void Trailing_zeros_beyond_scale_are_not_extra_precision()
    {
        Assert.True(Money.Create(100.500m, Idr).IsSuccess);
    }

    [Fact]
    public void Zero_scale_currency_rejects_fractions()
    {
        Assert.Equal(ErrorCodes.MoneyPrecisionExceedsScale, Money.Create(1.5m, IdrV2).Error.Code);
        Assert.True(Money.Create(1500m, IdrV2).IsSuccess);
    }

    [Fact]
    public void Negative_amount_is_rejected()
    {
        Assert.Equal(ErrorCodes.MoneyNegative, Money.Create(-0.01m, Idr).Error.Code);
    }

    [Fact]
    public void Amount_beyond_numeric_30_8_is_rejected()
    {
        Assert.Equal(ErrorCodes.MoneyOutOfRange, Money.Create(Money.ExclusiveUpperBound, Idr).Error.Code);
        Assert.True(Money.Create(Money.ExclusiveUpperBound - 1m, Idr).IsSuccess);
    }

    [Fact]
    public void Add_and_subtract_same_currency()
    {
        var principal = Rp(100_000m);
        var fee = Rp(2_500m);

        Assert.Equal(Rp(102_500m), principal.Add(fee).Value);
        Assert.Equal(Rp(97_500m), principal.Subtract(fee).Value);
    }

    [Fact]
    public void Subtract_below_zero_is_a_controlled_failure()
    {
        var result = Rp(80_000m).Subtract(Rp(100_000m));

        Assert.Equal(ErrorCodes.MoneyNegative, result.Error.Code);
    }

    [Fact]
    public void Arithmetic_with_different_currency_code_throws()
    {
        var usd = Money.Create(10m, Usd).Value;

        Assert.Throws<CurrencyMismatchException>(() => Rp(10m).Add(usd));
        Assert.Throws<CurrencyMismatchException>(() => Rp(10m).Subtract(usd));
        Assert.Throws<CurrencyMismatchException>(() => Rp(10m).IsGreaterThan(usd));
    }

    [Fact]
    public void Arithmetic_with_different_currency_definition_version_throws()
    {
        var v2 = Money.Create(10m, IdrV2).Value;

        Assert.Throws<CurrencyMismatchException>(() => Rp(10m).Add(v2));
    }

    [Fact]
    public void Equality_includes_currency_definition()
    {
        Assert.Equal(Rp(10m), Money.Create(10.00m, Idr).Value);
        Assert.NotEqual(Rp(10m), Money.Create(10m, IdrV2).Value);
        Assert.NotEqual(Rp(10m), Money.Create(10m, Usd).Value);
    }

    [Fact]
    public void Comparisons()
    {
        Assert.True(Rp(102_500m).IsGreaterThan(Rp(100_000m)));
        Assert.True(Rp(100_000m).IsGreaterThanOrEqualTo(Rp(100_000m)));
        Assert.True(Rp(1m).IsLessThan(Rp(2m)));
        Assert.True(Money.Zero(Idr).IsZero);
    }

    [Fact]
    public void Sum_of_empty_sequence_is_zero_and_sum_requires_same_currency()
    {
        Assert.Equal(Money.Zero(Idr), Money.Sum([], Idr).Value);
        Assert.Equal(Rp(6m), Money.Sum([Rp(1m), Rp(2m), Rp(3m)], Idr).Value);
        Assert.Throws<CurrencyMismatchException>(() => Money.Sum([Money.Create(1m, Usd).Value], Idr));
    }

    [Theory]
    [InlineData(100000, "100000.00")]
    [InlineData(0.5, "0.50")]
    [InlineData(0, "0.00")]
    public void Canonical_amount_string_is_padded_to_scale_and_culture_invariant(double raw, string expected)
    {
        Assert.Equal(expected, Rp((decimal)raw).ToCanonicalAmountString());
    }
}
