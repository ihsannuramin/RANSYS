using Ransys.Domain.Common;

namespace Ransys.Domain.Monetary;

/// <summary>
/// Snapshot identity of a versioned currency definition (Canonical Data Model §138–139):
/// code + version + scale. Effective ranges and status belong to configuration, not to the snapshot.
/// Transactions keep the definition they were created with forever (Ledger Posting Rule Matrix §50).
/// </summary>
public sealed record CurrencyDefinition
{
    /// <summary>DDL v1.1 <c>ck_currency_scale</c>: scale BETWEEN 0 AND 8.</summary>
    public const short MaxScale = 8;

    private CurrencyDefinition(string code, int version, short scale)
    {
        Code = code;
        Version = version;
        Scale = scale;
    }

    /// <summary>ISO-4217-style three-letter code, uppercase.</summary>
    public string Code { get; }

    public int Version { get; }

    /// <summary>Number of fractional digits allowed for amounts in this definition.</summary>
    public short Scale { get; }

    public static Result<CurrencyDefinition> Create(string? code, int version, short scale)
    {
        if (code is null || code.Length != 3 || !code.All(char.IsAsciiLetter))
        {
            return RansysError.Validation(
                ErrorCodes.CurrencyCodeInvalid, "Currency code must be three ASCII letters.", "currencyCode");
        }

        if (version <= 0)
        {
            return RansysError.Validation(
                ErrorCodes.CurrencyVersionInvalid, "Currency definition version must be positive.", "currencyDefinitionVersion");
        }

        if (scale is < 0 or > MaxScale)
        {
            return RansysError.Validation(
                ErrorCodes.CurrencyScaleInvalid, $"Currency scale must be between 0 and {MaxScale}.", "currencyScale");
        }

        return new CurrencyDefinition(code.ToUpperInvariant(), version, scale);
    }

    public override string ToString() => $"{Code}/v{Version}/s{Scale}";
}
