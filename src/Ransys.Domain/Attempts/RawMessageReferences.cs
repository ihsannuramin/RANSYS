using Ransys.Domain.Common;

namespace Ransys.Domain.Attempts;

/// <summary>
/// Opaque URI-like references to raw provider payloads stored outside the database
/// (Canonical Data Model §46, Architecture Spec §48). The payload itself is never embedded.
/// </summary>
public sealed record RawMessageReferences
{
    /// <summary>DDL v1.1 <c>raw_request_reference varchar(1000)</c>.</summary>
    public const int MaxReferenceLength = 1000;

    private RawMessageReferences(string? requestUri, string? responseUri)
    {
        RequestUri = requestUri;
        ResponseUri = responseUri;
    }

    public static RawMessageReferences None { get; } = new(null, null);

    public string? RequestUri { get; }

    public string? ResponseUri { get; }

    public static Result<RawMessageReferences> Create(string? requestUri, string? responseUri)
    {
        var error = Text.FirstError(
            Text.Optional(requestUri, "rawMessages.requestUri", MaxReferenceLength),
            Text.Optional(responseUri, "rawMessages.responseUri", MaxReferenceLength));

        return error is null ? new RawMessageReferences(requestUri, responseUri) : error;
    }
}
