using Ransys.Domain.Common;

namespace Ransys.Domain.Transactions;

/// <summary>
/// Generic but structured source/destination (Canonical Data Model §20–21).
/// Common identifiers are first-class; only non-common extension fields belong to <see cref="Metadata"/>.
/// </summary>
public sealed record TransactionEndpoint
{
    private TransactionEndpoint(
        EndpointType type,
        string identifier,
        string? institutionCode,
        string? accountReference,
        ExtensionMetadata metadata)
    {
        Type = type;
        Identifier = identifier;
        InstitutionCode = institutionCode;
        AccountReference = accountReference;
        Metadata = metadata;
    }

    public EndpointType Type { get; }

    public string Identifier { get; }

    public string? InstitutionCode { get; }

    public string? AccountReference { get; }

    public ExtensionMetadata Metadata { get; }

    public static Result<TransactionEndpoint> Create(
        EndpointType type,
        string? identifier,
        string? institutionCode = null,
        string? accountReference = null,
        ExtensionMetadata? metadata = null)
    {
        if (!Enum.IsDefined(type))
        {
            return RansysError.Validation(ErrorCodes.OutOfRange, "Unknown endpoint type.", "endpoint.type");
        }

        var error = Text.FirstError(
            Text.RequiredNotBlank(identifier, "endpoint.identifier"),
            Text.OptionalNotBlank(institutionCode, "endpoint.institutionCode"),
            Text.OptionalNotBlank(accountReference, "endpoint.accountReference"));

        return error is null
            ? new TransactionEndpoint(type, identifier!, institutionCode, accountReference, metadata ?? ExtensionMetadata.Empty)
            : error;
    }
}
