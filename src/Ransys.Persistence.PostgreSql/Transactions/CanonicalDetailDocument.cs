using System.Text.Json;
using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Transactions;

namespace Ransys.Persistence.PostgreSql.Transactions;

/// <summary>
/// JSON shape of <c>core.transactions.canonical_detail</c> (ADR-013): first-class canonical objects without a
/// dedicated column. Values are re-validated through domain factories when read.
/// TODO / Architecture Decision Required: customer identifier masking/tokenization (ERD v1.1 §36, §52).
/// </summary>
internal sealed record CanonicalDetailDocument(
    CustomerDocument? Customer,
    EndpointDocument? Source,
    EndpointDocument? Destination,
    ReferencesDocument References)
{
    public static CanonicalDetailDocument From(Transaction transaction) => new(
        transaction.Customer is { } c
            ? new CustomerDocument(c.CustomerId, c.ExternalCustomerReference, c.AccountNumber, c.PhoneNumber, c.Name, Metadata(c.Metadata))
            : null,
        Endpoint(transaction.Source),
        Endpoint(transaction.Destination),
        new ReferencesDocument(
            transaction.References.MerchantReference,
            transaction.References.Stan,
            transaction.References.Rrn,
            transaction.References.ProviderReference,
            transaction.References.ProviderStan,
            transaction.References.ProviderRrn,
            transaction.References.ExternalReferences
                .Select(r => new ExternalReferenceDocument(r.Type, r.Value, r.Source, r.IsPrimary))
                .ToList()));

    public Result<Customer?> ToCustomer()
    {
        if (Customer is null)
        {
            return Result<Customer?>.Success(null);
        }

        var metadata = ExtensionMetadata.Create(Customer.Metadata);
        if (metadata.IsFailure)
        {
            return metadata.Error;
        }

        var customer = Domain.Transactions.Customer.Create(
            Customer.CustomerId, Customer.ExternalCustomerReference, Customer.AccountNumber, Customer.PhoneNumber, Customer.Name, metadata.Value);
        return customer.IsSuccess ? customer.Value : customer.Error;
    }

    public static Result<TransactionEndpoint?> ToEndpoint(EndpointDocument? document)
    {
        if (document is null)
        {
            return Result<TransactionEndpoint?>.Success(null);
        }

        if (!CanonicalCodes.EndpointType.TryParse(document.Type, out var type))
        {
            return RansysError.Validation(ErrorCodes.OutOfRange, $"Unknown endpoint type '{document.Type}'.", "endpoint.type");
        }

        var metadata = ExtensionMetadata.Create(document.Metadata);
        if (metadata.IsFailure)
        {
            return metadata.Error;
        }

        var endpoint = TransactionEndpoint.Create(type, document.Identifier, document.InstitutionCode, document.AccountReference, metadata.Value);
        return endpoint.IsSuccess ? endpoint.Value : endpoint.Error;
    }

    public Result<TransactionReferences> ToReferences(string clientReference)
    {
        var external = new List<ExternalReference>();
        foreach (var document in References.ExternalReferences ?? [])
        {
            var reference = ExternalReference.Create(document.Type, document.Value, document.Source, document.IsPrimary);
            if (reference.IsFailure)
            {
                return reference.Error;
            }

            external.Add(reference.Value);
        }

        return TransactionReferences.Create(
            clientReference,
            References.MerchantReference,
            References.Stan,
            References.Rrn,
            References.ProviderReference,
            References.ProviderStan,
            References.ProviderRrn,
            external);
    }

    private static EndpointDocument? Endpoint(TransactionEndpoint? endpoint) =>
        endpoint is null
            ? null
            : new EndpointDocument(
                CanonicalCodes.EndpointType.ToCode(endpoint.Type),
                endpoint.Identifier,
                endpoint.InstitutionCode,
                endpoint.AccountReference,
                Metadata(endpoint.Metadata));

    private static Dictionary<string, JsonElement>? Metadata(ExtensionMetadata metadata) =>
        metadata.Count == 0 ? null : metadata.Values.ToDictionary(StringComparer.Ordinal);
}

internal sealed record CustomerDocument(
    string? CustomerId,
    string? ExternalCustomerReference,
    string? AccountNumber,
    string? PhoneNumber,
    string? Name,
    Dictionary<string, JsonElement>? Metadata);

internal sealed record EndpointDocument(
    string Type,
    string Identifier,
    string? InstitutionCode,
    string? AccountReference,
    Dictionary<string, JsonElement>? Metadata);

internal sealed record ReferencesDocument(
    string? MerchantReference,
    string? Stan,
    string? Rrn,
    string? ProviderReference,
    string? ProviderStan,
    string? ProviderRrn,
    List<ExternalReferenceDocument>? ExternalReferences);

internal sealed record ExternalReferenceDocument(string Type, string Value, string Source, bool IsPrimary);
