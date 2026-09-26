using System.Collections.Immutable;
using System.Text.Json;

namespace Ransys.Domain.Common;

/// <summary>
/// Governed product/provider extension data (Canonical Data Model §38–41, §19).
/// <list type="bullet">
/// <item>Keys must be namespaced: <c>product.&lt;code&gt;.&lt;field&gt;</c>, <c>provider.&lt;code&gt;.&lt;field&gt;</c>
/// or <c>extension.&lt;domain&gt;.&lt;field&gt;</c>. Unscoped keys (e.g. <c>amount</c>, <c>extra</c>) are rejected,
/// which also keeps canonical fields out of metadata.</item>
/// <item>Sensitive credentials (CVV, PIN, PIN block, password, secret, private key) are rejected.</item>
/// </list>
/// TODO / Architecture Decision Required: total size, key length and depth limits are set in the API/config
/// phase (Canonical Data Model §135).
/// </summary>
public sealed class ExtensionMetadata
{
    private static readonly string[] AllowedRoots = ["product", "provider", "extension"];

    private static readonly string[] SensitiveFieldNames =
    [
        "cvv", "cvv2", "cvc", "cvc2", "pin", "pinblock", "password", "passcode",
        "secret", "apisecret", "clientsecret", "privatekey", "credential", "credentials",
    ];

    private ExtensionMetadata(ImmutableSortedDictionary<string, JsonElement> values) => Values = values;

    public static ExtensionMetadata Empty { get; } =
        new(ImmutableSortedDictionary.Create<string, JsonElement>(StringComparer.Ordinal));

    /// <summary>Entries ordered by key (ordinal) for deterministic serialization.</summary>
    public ImmutableSortedDictionary<string, JsonElement> Values { get; }

    public int Count => Values.Count;

    public static Result<ExtensionMetadata> Create(IEnumerable<KeyValuePair<string, JsonElement>>? entries)
    {
        if (entries is null)
        {
            return Empty;
        }

        var builder = ImmutableSortedDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            var keyError = ValidateKey(key);
            if (keyError is not null)
            {
                return keyError;
            }

            if (!builder.TryAdd(key, value.Clone()))
            {
                return RansysError.Validation(ErrorCodes.MetadataKeyInvalid, $"Duplicate metadata key '{key}'.", key);
            }
        }

        return builder.Count == 0 ? Empty : new ExtensionMetadata(builder.ToImmutable());
    }

    private static RansysError? ValidateKey(string? key)
    {
        var segments = key?.Split('.') ?? [];
        var wellFormed = segments.Length >= 3
            && AllowedRoots.Contains(segments[0], StringComparer.Ordinal)
            && segments.All(s => s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'));

        if (!wellFormed)
        {
            return RansysError.Validation(
                ErrorCodes.MetadataKeyInvalid,
                $"Metadata key '{key}' must be namespaced as product.<code>.<field>, provider.<code>.<field> or extension.<domain>.<field>.",
                key);
        }

        var field = segments[^1].Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        if (SensitiveFieldNames.Contains(field, StringComparer.OrdinalIgnoreCase))
        {
            return RansysError.Validation(
                ErrorCodes.MetadataSensitiveKey,
                $"Metadata key '{key}' would carry sensitive credential data, which is not allowed in canonical metadata.",
                key);
        }

        return null;
    }
}
