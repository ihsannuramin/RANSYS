using System.Collections.Immutable;
using Ransys.Domain.Common;

namespace Ransys.Domain.Transactions;

/// <summary>
/// Extensible identifier such as ORDER_ID or BILL_NUMBER (Canonical Data Model §24).
/// </summary>
public sealed record ExternalReference
{
    private ExternalReference(string type, string value, string source, bool isPrimary)
    {
        Type = type;
        Value = value;
        Source = source;
        IsPrimary = isPrimary;
    }

    public string Type { get; }

    public string Value { get; }

    public string Source { get; }

    public bool IsPrimary { get; }

    public static Result<ExternalReference> Create(string? type, string? value, string? source, bool isPrimary)
    {
        var error = Text.FirstError(
            Text.RequiredNotBlank(type, "externalReference.type"),
            Text.RequiredNotBlank(value, "externalReference.value"),
            Text.RequiredNotBlank(source, "externalReference.source"));

        return error is null
            ? new ExternalReference(type!, value!, source!, isPrimary)
            : error;
    }
}

/// <summary>
/// Reference bag for a transaction (Canonical Data Model §22–23). STAN/RRN are not global keys,
/// may be absent, and provider values may differ from merchant-facing values.
/// Provider-issued identifiers are case-sensitive and stored exactly as received.
/// </summary>
public sealed record TransactionReferences
{
    // Lengths follow DDL v1.1 core.transaction_attempts (provider_reference 128, provider_stan 32, provider_rrn 64).
    public const int MaxReferenceLength = 128;
    public const int MaxStanLength = 32;
    public const int MaxRrnLength = 64;

    private TransactionReferences(
        string clientReference,
        string? merchantReference,
        string? stan,
        string? rrn,
        string? providerReference,
        string? providerStan,
        string? providerRrn,
        ImmutableArray<ExternalReference> externalReferences)
    {
        ClientReference = clientReference;
        MerchantReference = merchantReference;
        Stan = stan;
        Rrn = rrn;
        ProviderReference = providerReference;
        ProviderStan = providerStan;
        ProviderRrn = providerRrn;
        ExternalReferences = externalReferences;
    }

    public string ClientReference { get; }

    public string? MerchantReference { get; }

    public string? Stan { get; }

    public string? Rrn { get; }

    public string? ProviderReference { get; }

    public string? ProviderStan { get; }

    public string? ProviderRrn { get; }

    public ImmutableArray<ExternalReference> ExternalReferences { get; }

    public static Result<TransactionReferences> Create(
        string? clientReference,
        string? merchantReference = null,
        string? stan = null,
        string? rrn = null,
        string? providerReference = null,
        string? providerStan = null,
        string? providerRrn = null,
        IEnumerable<ExternalReference>? externalReferences = null)
    {
        var error = Text.FirstError(
            Text.Required(clientReference, "clientReference", TransactionIdentity.MaxClientReferenceLength),
            Text.Optional(merchantReference, "merchantReference", MaxReferenceLength),
            Text.Optional(stan, "stan", MaxStanLength),
            Text.Optional(rrn, "rrn", MaxRrnLength),
            Text.Optional(providerReference, "providerReference", MaxReferenceLength),
            Text.Optional(providerStan, "providerStan", MaxStanLength),
            Text.Optional(providerRrn, "providerRrn", MaxRrnLength));
        if (error is not null)
        {
            return error;
        }

        var external = externalReferences?.ToImmutableArray() ?? [];
        if (external.Any(r => r is null))
        {
            return RansysError.Validation(ErrorCodes.Required, "External references must not contain null entries.", "externalReferences");
        }

        return new TransactionReferences(
            clientReference!, merchantReference, stan, rrn, providerReference, providerStan, providerRrn, external);
    }

    /// <summary>Returns a copy carrying provider-issued identifiers, validated with the same rules.</summary>
    public Result<TransactionReferences> WithProviderReferences(
        string? providerReference, string? providerStan, string? providerRrn) =>
        Create(ClientReference, MerchantReference, Stan, Rrn, providerReference, providerStan, providerRrn, ExternalReferences);
}
