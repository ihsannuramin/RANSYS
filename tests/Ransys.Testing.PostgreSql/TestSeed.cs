using Dapper;
using Npgsql;

namespace Ransys.Testing.PostgreSql;

/// <summary>
/// Baseline reference data inserted once per test run: one merchant/channel/product, IDR v1 (scale 2),
/// two providers and one active routing config version.
/// </summary>
public sealed record TestSeed(
    Guid MerchantId,
    Guid ChannelId,
    Guid ProductId,
    Guid IdrV1CurrencyDefinitionId,
    Guid ProviderAId,
    Guid ProviderBId,
    Guid ConfigVersionId)
{
    public const string ProviderACode = "BANK_A";
    public const string ProviderAAdapter = "ransys-adapter-bank-a";
    public const string ProviderBCode = "BANK_B";
    public const string ProviderBAdapter = "ransys-adapter-bank-b";

    public static readonly DateTimeOffset SeedTime = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);

    public static async Task<TestSeed> CreateAsync(NpgsqlDataSource dataSource)
    {
        var seed = new TestSeed(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var now = SeedTime.UtcDateTime;

        await using var connection = await dataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO core.merchants VALUES (@MerchantId, 'MRC-TEST', 'Test Merchant', 'ACTIVE', @Now, @Now);
            INSERT INTO core.channels VALUES (@ChannelId, @MerchantId, 'API', 'REST', 'SIGNED_API', 'ACTIVE', @Now, @Now);
            INSERT INTO core.products VALUES (@ProductId, 'PLN-PREPAID', 'PLN Prepaid', 'BILLER', 'ACTIVE', NULL, @Now, @Now);
            INSERT INTO core.currency_definitions VALUES (@IdrV1CurrencyDefinitionId, 'IDR', 1, 2, NULL, @Now, NULL, 'ACTIVE', @Now);
            INSERT INTO integration.providers VALUES
                (@ProviderAId, 'BANK_A', 'Bank A', 'BANK', 'ransys-adapter-bank-a', 'REST', 'ACTIVE', @Now, @Now),
                (@ProviderBId, 'BANK_B', 'Bank B', 'BANK', 'ransys-adapter-bank-b', 'ISO8583', 'ACTIVE', @Now, @Now);
            INSERT INTO config.config_versions
                (config_version_id, config_domain, version_no, status, effective_from, created_by, created_at, activated_at)
            VALUES (@ConfigVersionId, 'ROUTING', 1, 'ACTIVE', @Now, @MerchantId, @Now, @Now);
            """,
            new
            {
                seed.MerchantId,
                seed.ChannelId,
                seed.ProductId,
                seed.IdrV1CurrencyDefinitionId,
                seed.ProviderAId,
                seed.ProviderBId,
                seed.ConfigVersionId,
                Now = now,
            });

        return seed;
    }
}
