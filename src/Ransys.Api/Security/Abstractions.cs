using Ransys.Domain;

namespace Ransys.Api.Security;

// ADR-022 (Proposed): request authentication abstractions. The RANSYS signed HTTP message profile is not specified yet,
// so production fails closed; a Development/Test-only authenticator exists for local work and tests.

/// <summary>The authenticated caller: the channel it acts as, the owning merchant and its client identity.</summary>
/// <param name="ClientId">Value of <c>X-Ransys-Client-Id</c> as authenticated.</param>
/// <param name="CertificateThumbprint">SHA-256 thumbprint of the mTLS client certificate, when one was presented.</param>
public sealed record ClientContext(ChannelId ChannelId, MerchantId MerchantId, string ClientId, string? CertificateThumbprint);

/// <summary>Security-relevant parts of one HTTP request, captured by <see cref="RequestAuthenticationMiddleware"/>.</summary>
public sealed record AuthenticationRequest(
    string Method,
    string PathAndQuery,
    string? ClientId,
    string? Timestamp,
    string? Nonce,
    string? ContentDigest,
    string? SignatureInput,
    string? Signature,
    string? CertificateThumbprint,
    ReadOnlyMemory<byte> Body);

/// <summary>Outcome of request authentication. A failure reason is for security logs only and is never sent to the client.</summary>
public sealed record AuthenticationResult(ClientContext? Client, string? FailureReason)
{
    public bool Succeeded => Client is not null;

    public static AuthenticationResult Success(ClientContext client) => new(client ?? throw new ArgumentNullException(nameof(client)), null);

    public static AuthenticationResult Fail(string reason) => new(null, reason);
}

/// <summary>Authenticates a request (mTLS identity, signature, timestamp, nonce, digest). Must never fake success.</summary>
public interface IRequestAuthenticationService
{
    Task<AuthenticationResult> AuthenticateAsync(AuthenticationRequest request, CancellationToken cancellationToken);
}

/// <summary>Verifies <c>Signature-Input</c> / <c>Signature</c> for the RANSYS signed HTTP message profile (TODO: ADR-022 profile).</summary>
public interface ISignatureVerifier
{
    Task<bool> VerifyAsync(AuthenticationRequest request, CancellationToken cancellationToken);
}

/// <summary>Anti-replay store for <c>X-Ransys-Nonce</c>. Unrelated to <c>Idempotency-Key</c> and <c>clientReference</c>.</summary>
public interface IReplayProtectionService
{
    /// <summary>Registers the nonce for the client; false when it was already seen and has not expired (replay).</summary>
    bool TryRegister(string clientId, string nonce, DateTimeOffset expiresAt);
}

/// <summary>Production default: no signature profile is implemented, so every signature fails verification.</summary>
public sealed class FailClosedSignatureVerifier : ISignatureVerifier
{
    public Task<bool> VerifyAsync(AuthenticationRequest request, CancellationToken cancellationToken) => Task.FromResult(false);
}

/// <summary>
/// Production default (ADR-022): until the signed HTTP message profile and client key store exist, every request is
/// rejected with 401. Never returns success.
/// </summary>
public sealed class FailClosedRequestAuthenticationService : IRequestAuthenticationService
{
    public Task<AuthenticationResult> AuthenticateAsync(AuthenticationRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(AuthenticationResult.Fail("Request authentication is not configured (ADR-022): failing closed."));
}
