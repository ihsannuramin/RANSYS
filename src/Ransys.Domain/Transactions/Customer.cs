using Ransys.Domain.Common;

namespace Ransys.Domain.Transactions;

/// <summary>
/// Cross-provider customer concepts only (Canonical Data Model §18–19). All fields are optional,
/// but a present value must not be blank. Sensitive credentials (PIN, CVV, secrets) are deliberately
/// not representable here or in <see cref="Metadata"/>.
/// TODO / Architecture Decision Required: tokenization/masking of customer identifiers (ERD v1.1 §52).
/// </summary>
public sealed record Customer
{
    private Customer(
        string? customerId,
        string? externalCustomerReference,
        string? accountNumber,
        string? phoneNumber,
        string? name,
        ExtensionMetadata metadata)
    {
        CustomerId = customerId;
        ExternalCustomerReference = externalCustomerReference;
        AccountNumber = accountNumber;
        PhoneNumber = phoneNumber;
        Name = name;
        Metadata = metadata;
    }

    public string? CustomerId { get; }

    public string? ExternalCustomerReference { get; }

    public string? AccountNumber { get; }

    public string? PhoneNumber { get; }

    public string? Name { get; }

    public ExtensionMetadata Metadata { get; }

    public static Result<Customer> Create(
        string? customerId = null,
        string? externalCustomerReference = null,
        string? accountNumber = null,
        string? phoneNumber = null,
        string? name = null,
        ExtensionMetadata? metadata = null)
    {
        var error = Text.FirstError(
            Text.OptionalNotBlank(customerId, "customer.customerId"),
            Text.OptionalNotBlank(externalCustomerReference, "customer.externalCustomerReference"),
            Text.OptionalNotBlank(accountNumber, "customer.accountNumber"),
            Text.OptionalNotBlank(phoneNumber, "customer.phoneNumber"),
            Text.OptionalNotBlank(name, "customer.name"));

        return error is null
            ? new Customer(customerId, externalCustomerReference, accountNumber, phoneNumber, name, metadata ?? ExtensionMetadata.Empty)
            : error;
    }
}
