using Ransys.Domain.Attempts;
using Ransys.Domain.Monetary;
using Ransys.Domain.Routing;
using Ransys.Domain.Transactions;

namespace Ransys.Domain.Tests;

internal static class TestData
{
    public static readonly CurrencyDefinition Idr = CurrencyDefinition.Create("IDR", 1, 2).Value;
    public static readonly CurrencyDefinition IdrV2 = CurrencyDefinition.Create("IDR", 2, 0).Value;
    public static readonly CurrencyDefinition Usd = CurrencyDefinition.Create("USD", 1, 2).Value;

    public static readonly MerchantId Merchant = new(Guid.Parse("0192a000-0000-7000-8000-000000000001"));
    public static readonly ChannelId Channel = new(Guid.Parse("0192a000-0000-7000-8000-000000000002"));
    public static readonly ProductId Product = new(Guid.Parse("0192a000-0000-7000-8000-000000000003"));
    public static readonly ProviderId ProviderA = new(Guid.Parse("0192a000-0000-7000-8000-00000000000a"));
    public static readonly ProviderId ProviderB = new(Guid.Parse("0192a000-0000-7000-8000-00000000000b"));

    public static readonly DateTimeOffset T0 = new(2026, 9, 26, 15, 30, 12, 123, TimeSpan.FromHours(7));

    public static Money Rp(decimal amount) => Money.Create(amount, Idr).Value;

    public static TransactionId NewTransactionId() => new(Guid.CreateVersion7());

    public static ProviderReference ProviderRefA { get; } =
        ProviderReference.Create(ProviderA, "BANK_A", "ransys-adapter-bank-a").Value;

    public static ProviderReference ProviderRefB { get; } =
        ProviderReference.Create(ProviderB, "BANK_B", "ransys-adapter-bank-b").Value;

    public static FingerprintInput FingerprintInput(decimal amount = 100_000m, string clientReference = "INV-001") =>
        new(
            Merchant,
            Channel,
            TransactionType.Payment,
            Product,
            Source: null,
            Destination: TransactionEndpoint.Create(EndpointType.BankAccount, "1234567890", "014").Value,
            Amount: Rp(amount),
            ClientReference: clientReference);

    public static TransactionAttempt StartAttempt(int attemptNumber = 1, ProviderReference? provider = null) =>
        TransactionAttempt.Start(
            new AttemptId(Guid.CreateVersion7()),
            NewTransactionId(),
            attemptNumber,
            AttemptType.Payment,
            provider ?? ProviderRefA,
            "corr-1",
            "trace-1",
            T0).Value;
}

internal static class RepositoryPaths
{
    public static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ransys.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate Ransys.sln above the test output directory.");
    }
}
