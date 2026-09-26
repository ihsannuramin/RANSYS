using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ransys.Domain.Common;
using Ransys.Domain.Monetary;

namespace Ransys.Domain.Transactions;

/// <summary>
/// Business fields that define "the same request" for duplicate detection
/// (Canonical Data Model §110, PRD §11.4). Volatile fields (timestamps, nonce, signature, trace ids)
/// are intentionally absent.
/// </summary>
public sealed record FingerprintInput(
    MerchantId MerchantId,
    ChannelId ChannelId,
    TransactionType TransactionType,
    ProductId ProductId,
    TransactionEndpoint? Source,
    TransactionEndpoint? Destination,
    Money? Amount,
    string ClientReference);

/// <summary>
/// Internal canonical duplicate detector (Canonical Data Model §8, §110–111).
/// Persisted as <c>v&lt;version&gt;:&lt;lowercase sha256 hex&gt;</c> (ADR-006).
/// Not the idempotency key and not the security nonce.
/// </summary>
public sealed record TransactionFingerprint
{
    public const int CurrentVersion = 1;

    /// <summary>DDL v1.1: <c>transaction_fingerprint varchar(128)</c>.</summary>
    public const int MaxPersistedLength = 128;

    private const int Sha256HexLength = 64;

    private TransactionFingerprint(int version, string value)
    {
        Version = version;
        Value = value;
    }

    public int Version { get; }

    /// <summary>Lowercase hexadecimal SHA-256 digest.</summary>
    public string Value { get; }

    public static TransactionFingerprint Compute(FingerprintInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var canonical = new StringBuilder();
        Append(canonical, "algorithm", "RANSYS-FP-1");
        Append(canonical, "merchant", input.MerchantId.ToString());
        Append(canonical, "channel", input.ChannelId.ToString());
        Append(canonical, "type", CanonicalCodes.TransactionType.ToCode(input.TransactionType));
        Append(canonical, "product", input.ProductId.ToString());
        AppendEndpoint(canonical, "source", input.Source);
        AppendEndpoint(canonical, "destination", input.Destination);
        Append(canonical, "amount", input.Amount?.ToCanonicalAmountString());
        Append(canonical, "currency", input.Amount?.CurrencyCode);
        Append(canonical, "currencyVersion", input.Amount?.CurrencyDefinitionVersion.ToString(CultureInfo.InvariantCulture));
        Append(canonical, "currencyScale", input.Amount?.CurrencyScale.ToString(CultureInfo.InvariantCulture));
        Append(canonical, "clientReference", input.ClientReference);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return new TransactionFingerprint(CurrentVersion, Convert.ToHexStringLower(digest));
    }

    /// <summary>Parses the persisted form <c>v&lt;version&gt;:&lt;hex&gt;</c>.</summary>
    public static Result<TransactionFingerprint> Parse(string? persisted)
    {
        var invalid = RansysError.Validation(
            ErrorCodes.FingerprintInvalid, "Fingerprint must have the form v<version>:<64 lowercase hex chars>.", "fingerprint");

        if (persisted is null || persisted.Length > MaxPersistedLength || !persisted.StartsWith('v'))
        {
            return invalid;
        }

        var separator = persisted.IndexOf(':', StringComparison.Ordinal);
        if (separator < 2
            || !int.TryParse(persisted.AsSpan(1, separator - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || version <= 0)
        {
            return invalid;
        }

        var value = persisted[(separator + 1)..];
        if (value.Length != Sha256HexLength || !value.All(char.IsAsciiHexDigitLower))
        {
            return invalid;
        }

        return new TransactionFingerprint(version, value);
    }

    public string ToPersistedString() => $"v{Version.ToString(CultureInfo.InvariantCulture)}:{Value}";

    public override string ToString() => ToPersistedString();

    // Length-prefixed fields make the serialization unambiguous regardless of value content.
    private static void Append(StringBuilder builder, string name, string? value)
    {
        builder.Append(name);
        if (value is null)
        {
            builder.Append("=-\n");
            return;
        }

        builder.Append('=')
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('\n');
    }

    private static void AppendEndpoint(StringBuilder builder, string name, TransactionEndpoint? endpoint)
    {
        Append(builder, name + ".type", endpoint is null ? null : CanonicalCodes.EndpointType.ToCode(endpoint.Type));
        Append(builder, name + ".identifier", endpoint?.Identifier);
        Append(builder, name + ".institution", endpoint?.InstitutionCode);
        Append(builder, name + ".account", endpoint?.AccountReference);
    }
}
