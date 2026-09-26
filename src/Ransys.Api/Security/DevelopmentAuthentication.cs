using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Ransys.Application;
using Ransys.Domain;
using Ransys.TransactionCore.Processing;

namespace Ransys.Api.Security;

/// <summary>Configuration section <c>Ransys:Auth</c> (ADR-022).</summary>
public sealed class ApiAuthOptions
{
    public const string Section = "Ransys:Auth";

    /// <summary>Enables <see cref="DevelopmentRequestAuthenticationService"/>; honored only in Development or Test.</summary>
    public bool AllowDevelopmentAuthentication { get; set; }

    /// <summary>Accepted clock skew of <c>X-Ransys-Timestamp</c> (default 5 minutes).</summary>
    public int TimestampToleranceSeconds { get; set; } = 300;

    /// <summary>
    /// When true (default) the development authenticator does not verify <c>Signature</c> (no profile exists yet) and logs
    /// a warning at startup. When false it calls <see cref="ISignatureVerifier"/>, whose default fails closed.
    /// </summary>
    public bool SkipSignatureVerification { get; set; } = true;

    /// <summary>Environments in which the development authenticator may run.</summary>
    public static bool IsDevelopmentAuthenticationEnvironment(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.IsDevelopment() || environment.IsEnvironment("Test");
    }
}

/// <summary>
/// In-memory nonce store for the development authenticator only: not shared between nodes and lost on restart, so it is
/// never acceptable in production (ADR-022). Expired entries are purged opportunistically.
/// </summary>
public sealed class InMemoryReplayProtectionService(IClock clock) : IReplayProtectionService
{
    private const int PurgeEvery = 1024;

    private readonly ConcurrentDictionary<(string ClientId, string Nonce), DateTimeOffset> _seen = new();
    private int _registrations;

    public bool TryRegister(string clientId, string nonce, DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(nonce);
        var now = clock.UtcNow;
        if (Interlocked.Increment(ref _registrations) % PurgeEvery == 0)
        {
            foreach (var entry in _seen)
            {
                if (entry.Value <= now)
                {
                    _seen.TryRemove(entry);
                }
            }
        }

        var key = (clientId, nonce);
        while (true)
        {
            if (_seen.TryAdd(key, expiresAt))
            {
                return true;
            }

            if (!_seen.TryGetValue(key, out var existing))
            {
                continue;
            }

            if (existing > now)
            {
                return false;
            }

            // An expired registration may be reused (its timestamp window has passed); replace it atomically.
            if (_seen.TryUpdate(key, expiresAt, existing))
            {
                return true;
            }
        }
    }
}

/// <summary>
/// Development/Test-only authenticator (ADR-022). <c>X-Ransys-Client-Id</c> is the channel UUID; the channel must be
/// ACTIVE and belong to an ACTIVE merchant. <c>X-Ransys-Timestamp</c> (RFC 3339) must be within the tolerance,
/// <c>X-Ransys-Nonce</c> must be unique per client, and a <c>Content-Digest</c> (when present) must match the body.
/// The signature is verified through <see cref="ISignatureVerifier"/> unless explicitly skipped by configuration.
/// It is registered only when the environment is Development/Test and the flag is set (checked in Program).
/// </summary>
public sealed partial class DevelopmentRequestAuthenticationService(
    IChannelDirectory channels,
    IReplayProtectionService replay,
    ISignatureVerifier signatures,
    IClock clock,
    ApiAuthOptions options) : IRequestAuthenticationService
{
    public const int MaxNonceLength = 128;

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();

    // Visible ASCII without spaces, so a nonce cannot smuggle separators into logs or keys.
    [GeneratedRegex(@"^[\x21-\x7E]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex NoncePattern();

    public async Task<AuthenticationResult> AuthenticateAsync(AuthenticationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ClientId is null || !Guid.TryParseExact(request.ClientId, "D", out var channelGuid) || channelGuid == Guid.Empty)
        {
            return AuthenticationResult.Fail("X-Ransys-Client-Id is missing or not a channel UUID.");
        }

        if (request.Timestamp is null || !TimestampPattern().IsMatch(request.Timestamp)
            || !DateTimeOffset.TryParse(request.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
        {
            return AuthenticationResult.Fail("X-Ransys-Timestamp is missing or not an RFC 3339 date-time.");
        }

        var now = clock.UtcNow;
        var tolerance = TimeSpan.FromSeconds(options.TimestampToleranceSeconds);
        if (timestamp < now - tolerance || timestamp > now + tolerance)
        {
            return AuthenticationResult.Fail("X-Ransys-Timestamp is outside the accepted window.");
        }

        if (request.Nonce is null || request.Nonce.Length > MaxNonceLength || !NoncePattern().IsMatch(request.Nonce))
        {
            return AuthenticationResult.Fail("X-Ransys-Nonce is missing or invalid.");
        }

        if (request.ContentDigest is not null && !ContentDigest.Verify(request.ContentDigest, request.Body.Span))
        {
            return AuthenticationResult.Fail("Content-Digest does not match the request body.");
        }

        if (!options.SkipSignatureVerification && !await signatures.VerifyAsync(request, cancellationToken))
        {
            return AuthenticationResult.Fail("Signature verification failed.");
        }

        var identity = await channels.FindActiveChannelAsync(new ChannelId(channelGuid), cancellationToken);
        if (identity is null)
        {
            return AuthenticationResult.Fail("Unknown or inactive channel.");
        }

        // Last, so a request rejected for another reason does not burn the nonce. A nonce stays registered for the whole
        // window in which its timestamp could still be accepted.
        if (!replay.TryRegister(channelGuid.ToString("D"), request.Nonce, timestamp + tolerance))
        {
            return AuthenticationResult.Fail("X-Ransys-Nonce was already used (replay).");
        }

        return AuthenticationResult.Success(new ClientContext(identity.ChannelId, identity.MerchantId, request.ClientId, request.CertificateThumbprint));
    }
}
