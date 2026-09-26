using System.Text.Json;
using Ransys.Domain.Common;

namespace Ransys.Domain.Tests.Common;

public sealed class ExtensionMetadataTests
{
    [Theory]
    [InlineData("product.pln.tariffCode")]
    [InlineData("provider.bankA.switchingField62")]
    [InlineData("extension.loyalty.member_tier")]
    [InlineData("provider.bank-a.field.sub")]
    public void Namespaced_keys_are_accepted(string key)
    {
        Assert.True(ExtensionMetadata.Create([Entry(key)]).IsSuccess);
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("merchantId")]
    [InlineData("extra")]
    [InlineData("provider.bankA")]
    [InlineData("custom.x.y")]
    [InlineData("product..field")]
    [InlineData("product.pln.tariff code")]
    [InlineData("")]
    public void Unscoped_or_malformed_keys_are_rejected(string key)
    {
        Assert.Equal(ErrorCodes.MetadataKeyInvalid, ExtensionMetadata.Create([Entry(key)]).Error.Code);
    }

    [Theory]
    [InlineData("provider.bankA.cvv")]
    [InlineData("provider.bankA.CVV2")]
    [InlineData("provider.bankA.pin")]
    [InlineData("provider.bankA.pin_block")]
    [InlineData("extension.auth.password")]
    [InlineData("extension.auth.private-key")]
    [InlineData("provider.bankA.apiSecret")]
    public void Sensitive_keys_are_rejected(string key)
    {
        Assert.Equal(ErrorCodes.MetadataSensitiveKey, ExtensionMetadata.Create([Entry(key)]).Error.Code);
    }

    [Fact]
    public void Duplicate_keys_are_rejected()
    {
        var result = ExtensionMetadata.Create([Entry("product.pln.a"), Entry("product.pln.a")]);

        Assert.Equal(ErrorCodes.MetadataKeyInvalid, result.Error.Code);
    }

    [Fact]
    public void Entries_are_ordered_and_null_or_empty_input_gives_empty()
    {
        var metadata = ExtensionMetadata.Create([Entry("product.pln.b"), Entry("product.pln.a")]).Value;

        Assert.Equal(["product.pln.a", "product.pln.b"], metadata.Values.Keys);
        Assert.Same(ExtensionMetadata.Empty, ExtensionMetadata.Create(null).Value);
        Assert.Same(ExtensionMetadata.Empty, ExtensionMetadata.Create([]).Value);
    }

    [Fact]
    public void Values_are_detached_from_the_source_document()
    {
        ExtensionMetadata metadata;
        using (var document = JsonDocument.Parse("""{"v":"x"}"""))
        {
            metadata = ExtensionMetadata.Create([
                new KeyValuePair<string, JsonElement>("product.pln.v", document.RootElement.GetProperty("v")),
            ]).Value;
        }

        Assert.Equal("x", metadata.Values["product.pln.v"].GetString());
    }

    private static KeyValuePair<string, JsonElement> Entry(string key) =>
        new(key, JsonSerializer.SerializeToElement("value"));
}
