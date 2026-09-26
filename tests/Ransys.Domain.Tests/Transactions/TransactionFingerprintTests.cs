using Ransys.Domain.Common;
using Ransys.Domain.Transactions;
using static Ransys.Domain.Tests.TestData;

namespace Ransys.Domain.Tests.Transactions;

public sealed class TransactionFingerprintTests
{
    [Fact]
    public void Same_business_payload_gives_same_fingerprint()
    {
        Assert.Equal(
            TransactionFingerprint.Compute(FingerprintInput()),
            TransactionFingerprint.Compute(FingerprintInput()));
    }

    [Fact]
    public void Equal_amounts_with_different_decimal_representation_give_same_fingerprint()
    {
        var a = FingerprintInput() with { Amount = Rp(100_000m) };
        var b = FingerprintInput() with { Amount = Rp(100_000.00m) };

        Assert.Equal(TransactionFingerprint.Compute(a), TransactionFingerprint.Compute(b));
    }

    [Fact]
    public void Different_amount_gives_different_fingerprint()
    {
        Assert.NotEqual(
            TransactionFingerprint.Compute(FingerprintInput(amount: 100_000m)),
            TransactionFingerprint.Compute(FingerprintInput(amount: 100_001m)));
    }

    [Fact]
    public void Each_business_field_affects_the_fingerprint()
    {
        var baseline = TransactionFingerprint.Compute(FingerprintInput());
        var otherDestination = TransactionEndpoint.Create(EndpointType.BankAccount, "9999999999", "014").Value;

        FingerprintInput[] variants =
        [
            FingerprintInput() with { MerchantId = new MerchantId(Guid.CreateVersion7()) },
            FingerprintInput() with { ChannelId = new ChannelId(Guid.CreateVersion7()) },
            FingerprintInput() with { ProductId = new ProductId(Guid.CreateVersion7()) },
            FingerprintInput() with { TransactionType = TransactionType.Purchase },
            FingerprintInput() with { Destination = otherDestination },
            FingerprintInput() with { Source = otherDestination },
            FingerprintInput() with { Amount = global::Ransys.Domain.Monetary.Money.Create(100_000m, IdrV2).Value },
            FingerprintInput() with { Amount = null },
            FingerprintInput(clientReference: "INV-002"),
        ];

        Assert.All(variants, v => Assert.NotEqual(baseline, TransactionFingerprint.Compute(v)));
    }

    [Fact]
    public void Field_boundaries_are_unambiguous()
    {
        // Moving characters between adjacent fields must not produce the same canonical form.
        var a = TransactionEndpoint.Create(EndpointType.BankAccount, "12", "3").Value;
        var b = TransactionEndpoint.Create(EndpointType.BankAccount, "1", "23").Value;

        Assert.NotEqual(
            TransactionFingerprint.Compute(FingerprintInput() with { Destination = a }),
            TransactionFingerprint.Compute(FingerprintInput() with { Destination = b }));
    }

    [Fact]
    public void Persisted_form_is_versioned_and_round_trips()
    {
        var fingerprint = TransactionFingerprint.Compute(FingerprintInput());
        var persisted = fingerprint.ToPersistedString();

        Assert.StartsWith("v1:", persisted, StringComparison.Ordinal);
        Assert.Equal(67, persisted.Length);
        Assert.True(persisted.Length <= TransactionFingerprint.MaxPersistedLength);
        Assert.Equal(fingerprint, TransactionFingerprint.Parse(persisted).Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1:abc")]
    [InlineData("v0:0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("vx:0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("v1:ABCDEF0000000000000000000000000000000000000000000000000000000000")]
    [InlineData("v1:00")]
    public void Malformed_persisted_fingerprint_is_rejected(string? persisted)
    {
        Assert.Equal(ErrorCodes.FingerprintInvalid, TransactionFingerprint.Parse(persisted).Error.Code);
    }
}
