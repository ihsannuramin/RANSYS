namespace Ransys.Domain.Tests;

public sealed class IdentifierTests
{
    [Fact]
    public void Empty_guid_is_rejected_for_every_identifier()
    {
        Assert.Throws<ArgumentException>(() => new TransactionId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new MerchantId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new ChannelId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new ProductId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new ProviderId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new WalletId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new AttemptId(Guid.Empty));
    }

    [Fact]
    public void Identifiers_have_value_equality_and_canonical_string_form()
    {
        var guid = Guid.CreateVersion7();

        Assert.Equal(new TransactionId(guid), new TransactionId(guid));
        Assert.Equal(guid.ToString("D"), new TransactionId(guid).ToString());
    }
}
