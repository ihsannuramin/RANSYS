using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ransys.Domain;
using Ransys.Testing;
using Ransys.Testing.PostgreSql;
using Ransys.TransactionCore.Processing;
using static Ransys.Api.Tests.ApiHarness;

namespace Ransys.Api.Tests;

/// <summary>Handoff §28 OpenAPI/API cases through the real HTTP pipeline, real PostgreSQL and scripted adapters.</summary>
[Collection(ApiCollection.Name)]
public sealed class TransactionApiTests(PostgresDatabaseFixture db, ApiFactory factory)
{
    private readonly ApiHarness _h = new(db, factory);

    [Fact]
    public async Task Valid_payment_is_200_success_0000_with_references_and_an_empty_data_object()
    {
        var s = await _h.NewScenario();
        s.Adapter.ThenSuccess(rrn: "RRN-42");

        var response = await _h.Post(s.Channel, "/api/v1/payments", Payment(s, merchantReference: "INV-1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await Json(response);
        Assert.Equal(("SUCCESS", "0000", "Success"), (json.GetProperty("transactionStatus").GetString(), json.GetProperty("responseCode").GetString(), json.GetProperty("responseMessage").GetString()));
        Assert.True(Guid.TryParseExact(json.GetProperty("ransysTransactionId").GetString(), "D", out _));
        Assert.Equal(JsonValueKind.Object, json.GetProperty("data").ValueKind);
        Assert.Equal(("INV-1", "RRN-42"), (json.GetProperty("references").GetProperty("merchantReference").GetString(), json.GetProperty("references").GetProperty("rrn").GetString()));
        var call = Assert.Single(s.Adapter.Calls);
        Assert.Equal((100_000m, "IDR"), (call.Request.Amount, call.Request.CurrencyCode));
        Assert.Equal((900_000m, 900_000m, 0m), await Balances(s));
    }

    [Fact]
    public async Task Provider_timeout_is_200_in_doubt_1002_and_the_hold_is_kept()
    {
        var s = await _h.NewScenario();
        s.Adapter.ThenHang();

        var response = await _h.Post(s.Channel, "/api/v1/payments", Payment(s));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await Json(response);
        Assert.Equal(("IN_DOUBT", "1002"), (json.GetProperty("transactionStatus").GetString(), json.GetProperty("responseCode").GetString()));
        Assert.Equal((1_000_000m, 900_000m, 100_000m), await Balances(s));
    }

    [Theory]
    [InlineData("1e5")]
    [InlineData("100.123456789")]
    [InlineData(" 100")]
    [InlineData("100 ")]
    [InlineData("+100")]
    [InlineData("-100")]
    [InlineData("0")]
    [InlineData("0.00")]
    [InlineData("01")]
    [InlineData("1.")]
    [InlineData(".5")]
    [InlineData("1,000")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("100\n")]
    [InlineData("99999999999999999999999999999999")]
    public async Task Malformed_or_non_positive_amount_is_400_2001_on_amount_value(string amount)
    {
        var s = await _h.NewScenario();

        var response = await _h.Post(s.Channel, "/api/v1/payments", Payment(s, amount));

        await AssertError(response, HttpStatusCode.BadRequest, "2001", "amount.value");
        Assert.Equal(0, s.Adapter.CallCount);
    }

    [Fact]
    public async Task Amount_as_a_json_number_or_with_more_decimals_than_the_currency_is_400()
    {
        var s = await _h.NewScenario();
        var number = $$"""{"clientReference":"{{NewReference()}}","productCode":"{{s.ProductCode}}","amount":{"value":100000,"currency":"IDR"},"requestTimestamp":"{{Now()}}"}""";

        await AssertError(await _h.PostRaw(s.Channel, "/api/v1/payments", number), HttpStatusCode.BadRequest, "2001", "amount.value");
        await AssertError(await _h.Post(s.Channel, "/api/v1/payments", Payment(s, "100.001")), HttpStatusCode.BadRequest, "2001", "amount.value");
        Assert.Equal(0, s.Adapter.CallCount);
    }

    [Theory]
    [InlineData("idr")]
    [InlineData("IDRX")]
    [InlineData("I1R")]
    public async Task Invalid_currency_is_400(string currency)
    {
        var s = await _h.NewScenario();
        var body = new { clientReference = NewReference(), productCode = s.ProductCode, amount = new { value = "1.00", currency }, requestTimestamp = Now() };

        await AssertError(await _h.Post(s.Channel, "/api/v1/payments", body), HttpStatusCode.BadRequest, "2001", "amount.currency");
    }

    [Fact]
    public async Task Missing_client_reference_is_400_2001()
    {
        var s = await _h.NewScenario();
        var body = new { productCode = s.ProductCode, amount = new { value = "1.00", currency = "IDR" }, requestTimestamp = Now() };

        await AssertError(await _h.Post(s.Channel, "/api/v1/payments", body), HttpStatusCode.BadRequest, "2001", "clientReference");
        Assert.Equal(0, s.Adapter.CallCount);
    }

    [Fact]
    public async Task Missing_or_timezone_less_request_timestamp_is_400()
    {
        var s = await _h.NewScenario();
        var missing = new { clientReference = NewReference(), productCode = s.ProductCode, amount = new { value = "1.00", currency = "IDR" } };
        var local = new { clientReference = NewReference(), productCode = s.ProductCode, amount = new { value = "1.00", currency = "IDR" }, requestTimestamp = "2026-09-27T10:00:00" };

        await AssertError(await _h.Post(s.Channel, "/api/v1/payments", missing), HttpStatusCode.BadRequest, "2001", "requestTimestamp");
        await AssertError(await _h.Post(s.Channel, "/api/v1/payments", local), HttpStatusCode.BadRequest, "2001", "requestTimestamp");
    }

    [Fact]
    public async Task Unknown_or_miscased_property_and_malformed_json_are_400()
    {
        var s = await _h.NewScenario();
        var unknown = $$"""{"clientReference":"{{NewReference()}}","productCode":"{{s.ProductCode}}","amount":{"value":"1.00","currency":"IDR"},"requestTimestamp":"{{Now()}}","feeOverride":"0"}""";
        var nested = $$"""{"clientReference":"{{NewReference()}}","productCode":"{{s.ProductCode}}","amount":{"value":"1.00","currency":"IDR","currencyVersion":2},"requestTimestamp":"{{Now()}}"}""";
        var miscased = $$"""{"ClientReference":"{{NewReference()}}","productCode":"{{s.ProductCode}}","amount":{"value":"1.00","currency":"IDR"},"requestTimestamp":"{{Now()}}"}""";

        await AssertError(await _h.PostRaw(s.Channel, "/api/v1/payments", unknown), HttpStatusCode.BadRequest, "2001");
        await AssertError(await _h.PostRaw(s.Channel, "/api/v1/payments", nested), HttpStatusCode.BadRequest, "2001");
        await AssertError(await _h.PostRaw(s.Channel, "/api/v1/payments", miscased), HttpStatusCode.BadRequest, "2001");
        await AssertError(await _h.PostRaw(s.Channel, "/api/v1/payments", "{\"clientReference\":"), HttpStatusCode.BadRequest, "2001");
        await AssertError(await _h.PostRaw(s.Channel, "/api/v1/payments", "[]"), HttpStatusCode.BadRequest, "2001");
        Assert.Equal(0, s.Adapter.CallCount);
    }

    [Fact]
    public async Task Invalid_uuid_on_get_is_400_and_unknown_uuid_is_404()
    {
        var s = await _h.NewScenario();

        await AssertError(await _h.Get(s.Channel, "/api/v1/transactions/not-a-uuid"), HttpStatusCode.BadRequest, "2001", "ransysTransactionId");
        await AssertError(await _h.Get(s.Channel, $"/api/v1/transactions/{Guid.CreateVersion7()}"), HttpStatusCode.NotFound, "2001");
    }

    [Fact]
    public async Task Duplicate_reference_with_the_same_payload_returns_the_same_transaction_without_a_second_provider_call()
    {
        var s = await _h.NewScenario();
        var body = Payment(s, reference: NewReference());

        var first = await Json(await _h.Post(s.Channel, "/api/v1/payments", body));
        var retry = await _h.Post(s.Channel, "/api/v1/payments", body, idempotencyKey: "retry-1");

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var second = await Json(retry);
        Assert.Equal(first.GetProperty("ransysTransactionId").GetString(), second.GetProperty("ransysTransactionId").GetString());
        Assert.Equal("SUCCESS", second.GetProperty("transactionStatus").GetString());
        Assert.Equal(1, s.Adapter.CallCount);
    }

    [Fact]
    public async Task Duplicate_reference_with_a_different_payload_is_409_2003()
    {
        var s = await _h.NewScenario();
        var reference = NewReference();
        await _h.Post(s.Channel, "/api/v1/payments", Payment(s, "100000.00", reference));

        var conflict = await _h.Post(s.Channel, "/api/v1/payments", Payment(s, "200000.00", reference));

        await AssertError(conflict, HttpStatusCode.Conflict, "2003", "clientReference");
        Assert.Equal(1, s.Adapter.CallCount);
    }

    [Fact]
    public async Task Insufficient_balance_is_200_failed_4001_without_a_provider_call()
    {
        var s = await _h.NewScenario(balance: 1_000m);

        var response = await _h.Post(s.Channel, "/api/v1/payments", Payment(s));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await Json(response);
        Assert.Equal(("FAILED", "4001"), (json.GetProperty("transactionStatus").GetString(), json.GetProperty("responseCode").GetString()));
        Assert.Equal(0, s.Adapter.CallCount);
        Assert.Equal((1_000m, 1_000m, 0m), await Balances(s));
    }

    [Fact]
    public async Task Database_unavailable_is_503_and_the_adapter_is_never_invoked()
    {
        var s = await _h.NewScenario();
        var unreachable = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = 1,
            Database = "RANSYS_PG_UNREACHABLE",
            Username = "nobody",
            Timeout = 2,
        }.ConnectionString;

        // Authentication resolves the channel without the database, so the request reaches Transaction Core.
        await using var broken = new CustomApiFactory("Test", unreachable, developmentAuthentication: true, services => services.AddSingleton<IChannelDirectory>(
            new FixedChannelDirectory(new ChannelIdentity(new ChannelId(s.Channel), new MerchantId(s.Merchant)))));
        broken.Registry.Register(new ProviderId(s.Provider), s.Adapter);
        var client = new ApiHarness(db, broken);

        var payment = await client.Post(s.Channel, "/api/v1/payments", Payment(s));
        var get = await client.Get(s.Channel, $"/api/v1/transactions/{Guid.CreateVersion7()}");

        await AssertError(payment, HttpStatusCode.ServiceUnavailable, "1001");
        await AssertError(get, HttpStatusCode.ServiceUnavailable, "1001");
        Assert.Equal(0, s.Adapter.CallCount);
        Assert.Equal((1_000_000m, 1_000_000m, 0m), await Balances(s));
    }

    [Fact]
    public async Task Transaction_detail_is_visible_only_to_the_owning_channel_and_exposes_no_internals()
    {
        var s = await _h.NewScenario();
        var other = await _h.NewScenario();
        s.Adapter.ThenSuccess(rrn: "RRN-9");
        var id = (await Json(await _h.Post(s.Channel, "/api/v1/payments", Payment(s, merchantReference: "INV-9")))).GetProperty("ransysTransactionId").GetString();

        var own = await _h.Get(s.Channel, $"/api/v1/transactions/{id}");
        var foreign = await _h.Get(other.Channel, $"/api/v1/transactions/{id}");

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        var json = await Json(own);
        Assert.Equal(
            ("PAYMENT", "SUCCESS", "POSTED", "0000"),
            (json.GetProperty("transactionType").GetString(), json.GetProperty("processingStatus").GetString(), json.GetProperty("financialStatus").GetString(), json.GetProperty("responseCode").GetString()));
        Assert.Equal(("UNMATCHED", "NOT_APPLICABLE"), (json.GetProperty("reconciliationStatus").GetString(), json.GetProperty("settlementStatus").GetString()));
        Assert.Equal("RRN-9", json.GetProperty("references").GetProperty("rrn").GetString());
        Assert.True(json.TryGetProperty("completedAt", out _));
        Assert.False(json.TryGetProperty("originalTransactionId", out _));
        var text = json.GetRawText();
        Assert.DoesNotContain(s.Provider.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(s.Wallet.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TX:", text, StringComparison.Ordinal);
        await AssertError(foreign, HttpStatusCode.NotFound, "2001");
    }

    [Fact]
    public async Task Reversal_is_a_child_with_its_own_id_and_the_original_is_unchanged_while_it_runs()
    {
        var s = await _h.NewScenario();
        var original = (await Json(await _h.Post(s.Channel, "/api/v1/payments", Payment(s)))).GetProperty("ransysTransactionId").GetString()!;
        s.Adapter.ThenHang();

        var response = await _h.Post(s.Channel, "/api/v1/reversals", new { clientReference = NewReference(), originalTransactionId = original, reason = "customer cancelled", requestTimestamp = Now() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var child = await Json(response);
        var childId = child.GetProperty("ransysTransactionId").GetString();
        Assert.NotEqual(original, childId);
        Assert.Equal(("IN_DOUBT", "1002"), (child.GetProperty("transactionStatus").GetString(), child.GetProperty("responseCode").GetString()));
        Assert.Equal("ReversalAsync", s.Adapter.Calls[1].Operation);
        var originalDetail = await Json(await _h.Get(s.Channel, $"/api/v1/transactions/{original}"));
        Assert.Equal(("SUCCESS", "POSTED"), (originalDetail.GetProperty("processingStatus").GetString(), originalDetail.GetProperty("financialStatus").GetString()));
        var childDetail = await Json(await _h.Get(s.Channel, $"/api/v1/transactions/{childId}"));
        Assert.Equal(("REVERSAL", original), (childDetail.GetProperty("transactionType").GetString(), childDetail.GetProperty("originalTransactionId").GetString()));
    }

    [Fact]
    public async Task Refund_is_a_child_that_partially_refunds_the_original_and_is_capped()
    {
        var s = await _h.NewScenario();
        var original = (await Json(await _h.Post(s.Channel, "/api/v1/payments", Payment(s)))).GetProperty("ransysTransactionId").GetString()!;

        var refund = await _h.Post(s.Channel, "/api/v1/refunds", Refund(original, "40000.00"));
        var tooMuch = await _h.Post(s.Channel, "/api/v1/refunds", Refund(original, "60000.01"));

        Assert.Equal(HttpStatusCode.OK, refund.StatusCode);
        var child = await Json(refund);
        Assert.NotEqual(original, child.GetProperty("ransysTransactionId").GetString());
        Assert.Equal("SUCCESS", child.GetProperty("transactionStatus").GetString());
        await AssertError(tooMuch, HttpStatusCode.BadRequest, "2001", "refundAmount.value");
        var detail = await Json(await _h.Get(s.Channel, $"/api/v1/transactions/{original}"));
        Assert.Equal(("PARTIALLY_REFUNDED", "PARTIALLY_REFUNDED"), (detail.GetProperty("processingStatus").GetString(), detail.GetProperty("financialStatus").GetString()));
        var childDetail = await Json(await _h.Get(s.Channel, $"/api/v1/transactions/{child.GetProperty("ransysTransactionId").GetString()}"));
        Assert.Equal(("REFUND", original), (childDetail.GetProperty("transactionType").GetString(), childDetail.GetProperty("originalTransactionId").GetString()));
        Assert.Equal(2, s.Adapter.CallCount);
        Assert.Equal((940_000m, 940_000m, 0m), await Balances(s));
    }

    [Fact]
    public async Task Void_endpoint_creates_a_void_child_and_flags_the_original_without_moving_money()
    {
        var s = await _h.NewScenario();
        var original = (await Json(await _h.Post(s.Channel, "/api/v1/payments", Payment(s)))).GetProperty("ransysTransactionId").GetString()!;

        var response = await _h.Post(s.Channel, "/api/v1/voids", new { clientReference = NewReference(), originalTransactionId = original, reason = "duplicate order", requestTimestamp = Now() });
        var second = await _h.Post(s.Channel, "/api/v1/voids", new { clientReference = NewReference(), originalTransactionId = original, reason = "again", requestTimestamp = Now() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var child = await Json(response);
        Assert.Equal("SUCCESS", child.GetProperty("transactionStatus").GetString());
        Assert.Equal("VoidAsync", s.Adapter.Calls[1].Operation);
        await AssertError(second, HttpStatusCode.BadRequest, "2001", "originalTransactionId");
        var detail = await Json(await _h.Get(s.Channel, $"/api/v1/transactions/{original}"));
        Assert.Equal(("SUCCESS", "POSTED", "EXCEPTION"),
            (detail.GetProperty("processingStatus").GetString(), detail.GetProperty("financialStatus").GetString(), detail.GetProperty("reconciliationStatus").GetString()));
        Assert.Equal((900_000m, 900_000m, 0m), await Balances(s));
    }

    [Fact]
    public async Task Unknown_or_foreign_original_is_400_on_original_transaction_id()
    {
        var s = await _h.NewScenario();
        var other = await _h.NewScenario();
        var foreign = (await Json(await _h.Post(other.Channel, "/api/v1/payments", Payment(other)))).GetProperty("ransysTransactionId").GetString()!;

        await AssertError(
            await _h.Post(s.Channel, "/api/v1/reversals", new { clientReference = NewReference(), originalTransactionId = foreign, reason = "x", requestTimestamp = Now() }),
            HttpStatusCode.BadRequest, "2001", "originalTransactionId");
        await AssertError(
            await _h.Post(s.Channel, "/api/v1/refunds", Refund(Guid.CreateVersion7().ToString(), "1.00")),
            HttpStatusCode.BadRequest, "2001", "originalTransactionId");
        await AssertError(
            await _h.Post(s.Channel, "/api/v1/voids", new { clientReference = NewReference(), originalTransactionId = "123", reason = "x", requestTimestamp = Now() }),
            HttpStatusCode.BadRequest, "2001", "originalTransactionId");
        Assert.Equal(0, s.Adapter.CallCount);
    }

    [Fact]
    public async Task Inquiry_and_transfer_endpoints_process_their_own_transaction_types()
    {
        var s = await _h.NewScenario();
        var endpoint = new { type = "BANK_ACCOUNT", identifier = "1234567890", institutionCode = "014" };

        var inquiry = await _h.Post(s.Channel, "/api/v1/inquiries", new { clientReference = NewReference(), productCode = s.ProductCode, destination = endpoint, requestTimestamp = Now() });
        var transfer = await _h.Post(s.Channel, "/api/v1/transfers", new
        {
            clientReference = NewReference(),
            productCode = s.ProductCode,
            amount = new { value = "50000.00", currency = "IDR" },
            source = new { type = "MERCHANT", identifier = "M-1" },
            destination = endpoint,
            requestTimestamp = Now(),
        });
        var badType = await _h.Post(s.Channel, "/api/v1/inquiries", new { clientReference = NewReference(), productCode = s.ProductCode, destination = new { type = "bank_account", identifier = "1" }, requestTimestamp = Now() });
        var noSource = await _h.Post(s.Channel, "/api/v1/transfers", new { clientReference = NewReference(), productCode = s.ProductCode, amount = new { value = "1.00", currency = "IDR" }, destination = endpoint, requestTimestamp = Now() });

        Assert.Equal(HttpStatusCode.OK, inquiry.StatusCode);
        Assert.Equal(HttpStatusCode.OK, transfer.StatusCode);
        Assert.Equal("SUCCESS", (await Json(transfer)).GetProperty("transactionStatus").GetString());
        Assert.Equal(["InquiryAsync", "TransferAsync"], s.Adapter.Calls.Select(c => c.Operation));
        await AssertError(badType, HttpStatusCode.BadRequest, "2001", "destination.type");
        await AssertError(noSource, HttpStatusCode.BadRequest, "2001", "source");
    }

    [Fact]
    public async Task Unknown_product_is_400_and_idempotency_key_longer_than_128_is_400()
    {
        var s = await _h.NewScenario();
        var product = new { clientReference = NewReference(), productCode = "NO-SUCH-PRODUCT", amount = new { value = "1.00", currency = "IDR" }, requestTimestamp = Now() };

        await AssertError(await _h.Post(s.Channel, "/api/v1/payments", product), HttpStatusCode.BadRequest, "2001", "productCode");
        await AssertError(await _h.Post(s.Channel, "/api/v1/payments", Payment(s), idempotencyKey: new string('k', 129)), HttpStatusCode.BadRequest, "2001", "Idempotency-Key");
        Assert.Equal(0, s.Adapter.CallCount);
    }

    internal static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string code, string? field = null)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"Expected {status}, got {response.StatusCode}: {body}");
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(code, json.RootElement.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("errorMessage").GetString()));
        Assert.True(json.RootElement.TryGetProperty("timestamp", out _));
        if (field is not null)
        {
            Assert.Equal(field, json.RootElement.GetProperty("field").GetString());
        }
    }

    private static object Refund(string original, string amount) => new
    {
        clientReference = NewReference(),
        originalTransactionId = original,
        refundAmount = new { value = amount, currency = "IDR" },
        reason = "customer request",
        requestTimestamp = Now(),
    };

    private Task<(decimal, decimal, decimal)> Balances(ApiScenario s) =>
        _h.Query<(decimal, decimal, decimal)>("SELECT ledger_balance, available_balance, reserved_balance FROM ledger.wallets WHERE wallet_id = @id", new { id = s.Wallet });

    private sealed class FixedChannelDirectory(ChannelIdentity identity) : IChannelDirectory
    {
        public Task<ChannelIdentity?> FindActiveChannelAsync(ChannelId channelId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ChannelIdentity?>(channelId == identity.ChannelId ? identity : null);
    }
}
