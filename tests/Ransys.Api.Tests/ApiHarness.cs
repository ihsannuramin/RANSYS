using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Ransys.Api.Contracts.V1;
using Ransys.Api.Security;
using Ransys.Application;
using Ransys.Domain;
using Ransys.Domain.Ledger;
using Ransys.Domain.Monetary;
using Ransys.Domain.Routing;
using Ransys.Ledger;
using Ransys.Testing;
using Ransys.Testing.PostgreSql;

namespace Ransys.Api.Tests;

/// <summary>One merchant + ACTIVE channel + funded IDR main wallet + product + one provider with a scripted adapter.</summary>
public sealed record ApiScenario(Guid Merchant, Guid Channel, Guid Wallet, Guid Product, string ProductCode, Guid Provider, ScriptedProviderAdapter Adapter);

/// <summary>Builds scenarios in the test database and sends development-authenticated requests to the API host.</summary>
public sealed class ApiHarness(PostgresDatabaseFixture db, ApiFactory factory)
{
    public static readonly CurrencyDefinition Idr = CurrencyDefinition.Create("IDR", 1, 2).Value;

    private HttpClient? _http;

    public HttpClient Http => _http ??= factory.CreateClient();

    public async Task<ApiScenario> NewScenario(decimal balance = 1_000_000m)
    {
        var merchant = Guid.CreateVersion7();
        var channel = Guid.CreateVersion7();
        var wallet = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        var provider = Guid.CreateVersion7();
        var productCode = $"PRD-{product:N}"[..30];
        await Execute(
            """
            INSERT INTO core.merchants VALUES (@merchant, @merchantCode, 'API Test Merchant', 'ACTIVE', now(), now());
            INSERT INTO core.channels VALUES (@channel, @merchant, 'API', 'REST', 'SIGNED_API', 'ACTIVE', now(), now());
            INSERT INTO core.products VALUES (@product, @productCode, 'API Test Product', 'BILLER', 'ACTIVE', NULL, now(), now());
            INSERT INTO ledger.wallets
                (wallet_id, merchant_id, product_id, currency_definition_id, ledger_balance, available_balance,
                 reserved_balance, status, created_at, updated_at)
            VALUES (@wallet, @merchant, NULL, @currency, 0, 0, 0, 'ACTIVE', now(), now());
            INSERT INTO integration.providers VALUES (@provider, @providerCode, 'API Test Provider', 'BANK', 'ransys-adapter-test', 'REST', 'ACTIVE', now(), now());
            INSERT INTO config.routing_routes VALUES (@route, @config, @product, NULL, @provider, 1, true, now());
            INSERT INTO integration.provider_operational_state VALUES (@provider, true, NULL, NULL, 'HEALTHY', 'CLOSED', now(), now());
            INSERT INTO integration.provider_capabilities SELECT @provider, c, true, '{}'::jsonb, now() FROM unnest(@capabilities) AS c;
            """,
            new
            {
                merchant,
                merchantCode = $"MRC-{merchant:N}",
                channel,
                product,
                productCode,
                wallet,
                currency = db.Seed.IdrV1CurrencyDefinitionId,
                provider,
                providerCode = $"PRV-{provider:N}"[..30],
                route = Guid.CreateVersion7(),
                config = db.Seed.ConfigVersionId,
                capabilities = ProviderCapabilities.All.ToArray(),
            });

        if (balance > 0)
        {
            var sessions = factory.Services.GetRequiredService<IDatabaseSessionFactory>();
            var ledger = factory.Services.GetRequiredService<ILedgerPostingService>();
            await using var session = await sessions.BeginAsync();
            var topUp = await ledger.PostTopUpAsync(
                session, new TopUpRequest(new WalletId(wallet), $"TP-{Guid.NewGuid():N}", Money.Create(balance, Idr).Value, null, LedgerActor.System));
            Assert.True(topUp.IsSuccess, topUp.ToString());
            await session.CommitAsync();
        }

        var adapter = new ScriptedProviderAdapter();
        factory.Registry.Register(new ProviderId(provider), adapter);
        return new ApiScenario(merchant, channel, wallet, product, productCode, provider, adapter);
    }

    public static string NewReference() => $"REF-{Guid.CreateVersion7():N}";

    public static object Payment(ApiScenario s, string amount = "100000.00", string? reference = null, string? merchantReference = null) => new
    {
        clientReference = reference ?? NewReference(),
        productCode = s.ProductCode,
        amount = new { value = amount, currency = "IDR" },
        merchantReference,
        requestTimestamp = Now(),
    };

    public static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    public Task<HttpResponseMessage> Post(Guid channel, string path, object body, string? idempotencyKey = null, Action<HttpRequestMessage>? tweak = null) =>
        PostRaw(channel, path, JsonSerializer.Serialize(body, ApiJson.Options), idempotencyKey, tweak);

    public Task<HttpResponseMessage> PostRaw(Guid channel, string path, string json, string? idempotencyKey = null, Action<HttpRequestMessage>? tweak = null)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        Sign(request, channel);
        request.Headers.Add("Content-Digest", ContentDigest.Create(bytes));
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        tweak?.Invoke(request);
        return Http.SendAsync(request);
    }

    public Task<HttpResponseMessage> Get(Guid channel, string path, Action<HttpRequestMessage>? tweak = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        Sign(request, channel);
        tweak?.Invoke(request);
        return Http.SendAsync(request);
    }

    public static void Sign(HttpRequestMessage request, Guid channel, DateTimeOffset? timestamp = null, string? nonce = null)
    {
        request.Headers.Remove("X-Ransys-Client-Id");
        request.Headers.Remove("X-Ransys-Timestamp");
        request.Headers.Remove("X-Ransys-Nonce");
        request.Headers.Add("X-Ransys-Client-Id", channel.ToString("D"));
        request.Headers.Add("X-Ransys-Timestamp", (timestamp ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture));
        request.Headers.Add("X-Ransys-Nonce", nonce ?? Guid.NewGuid().ToString("N"));
    }

    public static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public async Task<T> Query<T>(string sql, object parameters)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<T>(sql, parameters);
    }

    public async Task Execute(string sql, object parameters)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(sql, parameters);
    }
}
