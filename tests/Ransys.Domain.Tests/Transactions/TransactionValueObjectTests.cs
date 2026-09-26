using System.Text.Json;
using Ransys.Domain.Common;
using Ransys.Domain.Transactions;
using static Ransys.Domain.Tests.TestData;

namespace Ransys.Domain.Tests.Transactions;

public sealed class TransactionIdentityTests
{
    private static readonly TransactionFingerprint Fingerprint = TransactionFingerprint.Compute(FingerprintInput());

    [Fact]
    public void Creates_identity_with_optional_fields()
    {
        var id = NewTransactionId();
        var original = NewTransactionId();

        var identity = TransactionIdentity.Create(id, "INV-001", "idem-1", Fingerprint, original).Value;

        Assert.Equal(id, identity.RansysTransactionId);
        Assert.Equal("INV-001", identity.ClientReference);
        Assert.Equal("idem-1", identity.IdempotencyKey);
        Assert.Equal(original, identity.OriginalTransactionId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Client_reference_is_mandatory(string? clientReference)
    {
        var result = TransactionIdentity.Create(NewTransactionId(), clientReference, null, Fingerprint);

        Assert.Equal(ErrorCodes.Required, result.Error.Code);
        Assert.Equal("clientReference", result.Error.Field);
    }

    [Fact]
    public void Client_reference_longer_than_128_is_rejected()
    {
        var result = TransactionIdentity.Create(NewTransactionId(), new string('A', 129), null, Fingerprint);

        Assert.Equal(ErrorCodes.TooLong, result.Error.Code);
        Assert.True(TransactionIdentity.Create(NewTransactionId(), new string('A', 128), null, Fingerprint).IsSuccess);
    }

    [Fact]
    public void Client_reference_is_not_trimmed_or_altered()
    {
        Assert.Equal(" INV-1", TransactionIdentity.Create(NewTransactionId(), " INV-1", null, Fingerprint).Value.ClientReference);
    }

    [Fact]
    public void Blank_idempotency_key_is_rejected_but_null_is_allowed()
    {
        Assert.Equal(ErrorCodes.Required, TransactionIdentity.Create(NewTransactionId(), "INV", " ", Fingerprint).Error.Code);
        Assert.Null(TransactionIdentity.Create(NewTransactionId(), "INV", null, Fingerprint).Value.IdempotencyKey);
    }

    [Fact]
    public void Transaction_cannot_be_its_own_original()
    {
        var id = NewTransactionId();

        Assert.Equal(
            ErrorCodes.OriginalTransactionSelfReference,
            TransactionIdentity.Create(id, "INV", null, Fingerprint, id).Error.Code);
    }
}

public sealed class TransactionReferencesTests
{
    [Fact]
    public void Creates_references_and_adds_provider_identifiers()
    {
        var external = ExternalReference.Create("INVOICE_ID", "INV-77", "MERCHANT", isPrimary: true).Value;
        var references = TransactionReferences.Create("INV-001", stan: "000123", rrn: "RRN1", externalReferences: [external]).Value;

        var withProvider = references.WithProviderReferences("PRV-9", "654321", "PRRN").Value;

        Assert.Equal("PRV-9", withProvider.ProviderReference);
        Assert.Equal("000123", withProvider.Stan);
        Assert.Single(withProvider.ExternalReferences);
        Assert.Null(references.ProviderReference);
    }

    [Fact]
    public void Length_limits_follow_ddl()
    {
        Assert.Equal(ErrorCodes.TooLong, TransactionReferences.Create("INV", providerStan: new string('1', 33)).Error.Code);
        Assert.Equal(ErrorCodes.TooLong, TransactionReferences.Create("INV", providerRrn: new string('1', 65)).Error.Code);
        Assert.Equal(ErrorCodes.TooLong, TransactionReferences.Create("INV", providerReference: new string('1', 129)).Error.Code);
    }

    [Fact]
    public void Provider_references_keep_their_case()
    {
        Assert.Equal("AbC-123", TransactionReferences.Create("INV", providerReference: "AbC-123").Value.ProviderReference);
    }

    [Fact]
    public void External_reference_requires_type_value_and_source()
    {
        Assert.Equal(ErrorCodes.Required, ExternalReference.Create("", "v", "s", false).Error.Code);
        Assert.Equal(ErrorCodes.Required, ExternalReference.Create("t", null, "s", false).Error.Code);
        Assert.Equal(ErrorCodes.Required, ExternalReference.Create("t", "v", " ", false).Error.Code);
    }
}

public sealed class EndpointAndCustomerTests
{
    [Fact]
    public void Endpoint_requires_identifier()
    {
        Assert.Equal(ErrorCodes.Required, TransactionEndpoint.Create(EndpointType.Wallet, " ").Error.Code);
        Assert.Equal(ErrorCodes.Required, TransactionEndpoint.Create(EndpointType.Wallet, "W1", institutionCode: "").Error.Code);
        Assert.Equal(ErrorCodes.OutOfRange, TransactionEndpoint.Create((EndpointType)99, "W1").Error.Code);
    }

    [Fact]
    public void Endpoint_defaults_to_empty_metadata()
    {
        Assert.Same(ExtensionMetadata.Empty, TransactionEndpoint.Create(EndpointType.Biller, "PLN").Value.Metadata);
    }

    [Fact]
    public void Customer_fields_are_optional_but_not_blank()
    {
        Assert.True(Customer.Create().IsSuccess);
        Assert.Equal("customer.phoneNumber", Customer.Create(phoneNumber: "").Error.Field);
    }

    [Fact]
    public void Customer_metadata_cannot_carry_pin_or_cvv()
    {
        var metadata = ExtensionMetadata.Create([
            new KeyValuePair<string, JsonElement>("provider.bankA.pinBlock", JsonSerializer.SerializeToElement("1234")),
        ]);

        Assert.Equal(ErrorCodes.MetadataSensitiveKey, metadata.Error.Code);
    }
}
