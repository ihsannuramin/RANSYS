using System.Security.Cryptography;
using Ransys.Api.Errors;

namespace Ransys.Api.Security;

/// <summary>
/// Authenticates every <c>/api/v1</c> request before any endpoint runs (ADR-022). It reads the security headers
/// (<c>X-Ransys-Client-Id</c>, <c>X-Ransys-Timestamp</c>, <c>X-Ransys-Nonce</c>, <c>Content-Digest</c>,
/// <c>Signature-Input</c>, <c>Signature</c>) and the mTLS client certificate, buffers the body once (so the digest is
/// computed over the exact bytes the endpoint parses) and stores the <see cref="ClientContext"/> for the endpoint.
/// Failure ⇒ 401 / 3001 with no reason disclosed; the reason goes to the log. If the identity store is unavailable the
/// request fails closed with 503.
/// </summary>
public sealed class RequestAuthenticationMiddleware(RequestDelegate next, ILogger<RequestAuthenticationMiddleware> logger)
{
    public const int MaxBodyBytes = 1024 * 1024;

    private static readonly object ClientKey = new();
    private static readonly object BodyKey = new();

    public static ClientContext GetClient(HttpContext context) =>
        context.Items.TryGetValue(ClientKey, out var value) && value is ClientContext client
            ? client
            : throw new InvalidOperationException("The request was not authenticated; RequestAuthenticationMiddleware must run first.");

    public static ReadOnlyMemory<byte> GetBody(HttpContext context) =>
        context.Items.TryGetValue(BodyKey, out var value) && value is ReadOnlyMemory<byte> body ? body : ReadOnlyMemory<byte>.Empty;

    public async Task InvokeAsync(HttpContext context, IRequestAuthenticationService authentication)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authentication);
        if (!context.Request.Path.StartsWithSegments("/api/v1", StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        var body = await ReadBodyAsync(context.Request, context.RequestAborted);
        if (body is null)
        {
            await ApiError.Validation($"Request body exceeds {MaxBodyBytes} bytes.", null).WriteAsync(context);
            return;
        }

        var headers = context.Request.Headers;
        var certificate = context.Connection.ClientCertificate;
        var request = new AuthenticationRequest(
            context.Request.Method,
            context.Request.Path.Value + context.Request.QueryString.Value,
            Single(headers["X-Ransys-Client-Id"]),
            Single(headers["X-Ransys-Timestamp"]),
            Single(headers["X-Ransys-Nonce"]),
            Single(headers["Content-Digest"]),
            Single(headers["Signature-Input"]),
            Single(headers["Signature"]),
            certificate?.GetCertHashString(HashAlgorithmName.SHA256),
            body.Value);

        AuthenticationResult result;
        try
        {
            result = await authentication.AuthenticateAsync(request, context.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Request authentication could not complete; failing closed with 503.");
            await ApiError.DependencyUnavailable().WriteAsync(context);
            return;
        }

        if (!result.Succeeded)
        {
            logger.LogWarning("Request authentication failed for {Method} {Path}: {Reason}", request.Method, context.Request.Path.Value, result.FailureReason);
            await ApiError.Unauthorized().WriteAsync(context);
            return;
        }

        context.Items[ClientKey] = result.Client;
        context.Items[BodyKey] = body.Value;
        await next(context);
    }

    // A repeated security header is ambiguous and therefore treated as missing.
    private static string? Single(Microsoft.Extensions.Primitives.StringValues values) => values.Count == 1 ? values[0] : null;

    private static async Task<ReadOnlyMemory<byte>?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return new ReadOnlyMemory<byte>(buffer.ToArray());
    }
}
