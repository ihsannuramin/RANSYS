using System.Security.Cryptography;

namespace Ransys.Api.Security;

/// <summary>
/// RFC 9530 <c>Content-Digest</c> (a structured-field dictionary, RFC 8941) with the <c>sha-256</c> algorithm, e.g.
/// <c>sha-256=:X48E9qOokqqrvdts8nOJRJN3OWDUoyWxBf7kbu9DBPE=:</c>. Other algorithms may be listed but are ignored; the
/// header is valid only when a <c>sha-256</c> member is present and matches the exact request body bytes.
/// </summary>
public static class ContentDigest
{
    public const string Sha256 = "sha-256";

    /// <summary>The header value for <paramref name="body"/> (used by clients and tests).</summary>
    public static string Create(ReadOnlySpan<byte> body) => $"{Sha256}=:{Convert.ToBase64String(SHA256.HashData(body))}:";

    /// <summary>True only when the header parses and its sha-256 digest equals SHA-256(<paramref name="body"/>).</summary>
    public static bool Verify(string header, ReadOnlySpan<byte> body)
    {
        if (!TryGetSha256(header, out var expected))
        {
            return false;
        }

        Span<byte> actual = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(body, actual);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>Extracts the sha-256 byte sequence; false when the header is malformed, lacks sha-256 or repeats it.</summary>
    public static bool TryGetSha256(string? header, out byte[] digest)
    {
        digest = [];
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        byte[]? found = null;
        foreach (var rawMember in header.Split(','))
        {
            var member = rawMember.Trim(' ', '\t');
            var equals = member.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                return false;
            }

            var key = member[..equals];
            var value = member[(equals + 1)..];
            var parameters = value.IndexOf(';', StringComparison.Ordinal);
            if (parameters >= 0)
            {
                value = value[..parameters];
            }

            // Byte sequence: ":" base64 ":" (RFC 8941 §3.3.5).
            if (value.Length < 2 || value[0] != ':' || value[^1] != ':')
            {
                return false;
            }

            if (!string.Equals(key, Sha256, StringComparison.Ordinal))
            {
                continue;
            }

            if (found is not null)
            {
                return false;
            }

            var buffer = new byte[SHA256.HashSizeInBytes];
            if (!Convert.TryFromBase64String(value[1..^1], buffer, out var written) || written != SHA256.HashSizeInBytes)
            {
                return false;
            }

            found = buffer;
        }

        if (found is null)
        {
            return false;
        }

        digest = found;
        return true;
    }
}
