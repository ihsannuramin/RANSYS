using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Ransys.Adapter.Contracts.V1;

namespace Ransys.Adapter.Tests;

internal static class TestData
{
    public static readonly Guid ProviderId = Guid.Parse("0190a1b2-0000-7000-8000-000000000001");
    public static readonly Guid TransactionId = Guid.Parse("0190a1b2-0000-7000-8000-000000000002");
    public static readonly Guid OriginalId = Guid.Parse("0190a1b2-0000-7000-8000-000000000003");
    public static readonly Guid AttemptId = Guid.Parse("0190a1b2-0000-7000-8000-000000000004");
    public static readonly Guid EndpointProfileId = Guid.Parse("0190a1b2-0000-7000-8000-000000000005");
    public static readonly DateTimeOffset Now = new(2026, 9, 27, 10, 15, 30, 123, TimeSpan.Zero);

    public static ProviderIdentity Identity { get; } = new(ProviderId, "BANK_A", "adapter-bank-a");

    public static IReadOnlyDictionary<string, JsonElement> Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    public static ProviderTransactionReferences References(bool full = true) => full
        ? new ProviderTransactionReferences(
            "CLIENT-REF-001",
            "MERCH-REF-9",
            "000123",
            "123456789012",
            "PRV-REF-77",
            "654321",
            "210987654321",
            new Dictionary<string, string> { ["BILLER_REF"] = "B-1", ["VA_NUMBER"] = "8808123" })
        : new ProviderTransactionReferences("CLIENT-REF-002", null, null, null, null, null, null, new Dictionary<string, string>());

    public static ProviderTransactionRequest Request(
        string transactionType = "PAYMENT",
        decimal? amount = 123456789.12345678m,
        TimeSpan? connectTimeout = null,
        TimeSpan? readTimeout = null) =>
        new(
            TransactionId,
            OriginalId,
            AttemptId,
            transactionType,
            "PLN-PREPAID-50K",
            amount,
            amount is null ? null : "IDR",
            amount is null ? null : 1,
            Json("""{"customerId":"C-1","externalCustomerReference":"EXT-1","accountNumber":"1234567890","phoneNumber":"+628123","name":"Budi","metadata":{"tier":"gold","n":3}}"""),
            Json("""{"type":"WALLET","identifier":"W-1","metadata":{}}"""),
            Json("""{"type":"BANK_ACCOUNT","identifier":"9988776655","institutionCode":"014","accountReference":"ACC-1","metadata":{"branch":"JKT","flags":[true,false,null],"ratio":0.1}}"""),
            References(),
            new ProviderExecutionContext(
                Identity,
                EndpointProfileId,
                7,
                new ProviderTimeoutPolicy(connectTimeout ?? TimeSpan.FromSeconds(2), readTimeout ?? TimeSpan.FromSeconds(3), 0, TimeSpan.FromMilliseconds(500), "NONE", true, false),
                new ProviderConcurrencyPolicy(50, null, 100),
                new HashSet<string> { ProviderCapabilityCodes.Payment, ProviderCapabilityCodes.StatusCheck },
                "auth-profile/bank-a",
                42),
            new CorrelationContext(TransactionId, "corr-1", "trace-1"),
            Json("""{"channel":"API","amountText":"123456789.12345678"}"""));

    public static ProviderTransactionRequest MinimalRequest() =>
        new(
            TransactionId,
            null,
            AttemptId,
            "INQUIRY",
            "PLN-INQ",
            null,
            null,
            null,
            new Dictionary<string, JsonElement>(),
            new Dictionary<string, JsonElement>(),
            new Dictionary<string, JsonElement>(),
            References(full: false),
            new ProviderExecutionContext(
                Identity,
                null,
                null,
                new ProviderTimeoutPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 0, TimeSpan.Zero, string.Empty, false, false),
                new ProviderConcurrencyPolicy(null, null, null),
                new HashSet<string>(),
                null,
                1),
            new CorrelationContext(TransactionId, "corr-2", "trace-2"),
            new Dictionary<string, JsonElement>());

    /// <summary>A result that satisfies <see cref="ProviderResultRules"/> for <paramref name="outcome"/>.</summary>
    public static ProviderResult Result(ProviderOutcome outcome, bool full = true)
    {
        var (finality, status, sent, code) = outcome switch
        {
            ProviderOutcome.Success => (ResultFinality.Definitive, TransportStatus.Response, true, "0000"),
            ProviderOutcome.Failed => (ResultFinality.Definitive, TransportStatus.Response, true, "4001"),
            ProviderOutcome.Pending => (ResultFinality.NonFinal, TransportStatus.Response, true, "1002"),
            ProviderOutcome.InDoubt => (ResultFinality.Ambiguous, TransportStatus.Timeout, true, "1002"),
            _ => (ResultFinality.NotApplicable, TransportStatus.NotSent, false, "5001"),
        };

        return full
            ? new ProviderResult(
                outcome,
                finality,
                new ProviderTransportResult(
                    status,
                    sent,
                    TimeSpan.FromMilliseconds(12),
                    TimeSpan.FromMilliseconds(345),
                    new ProviderError(ProviderErrorCategories.ProviderDecline, "51", "Declined", false, "RAW-51")),
                code,
                "00",
                "Approved",
                References(),
                Json("""{"token":"1234-5678","amount":"123456789.12345678","count":2,"nested":{"ok":true}}"""),
                new ProviderRetryHint(false, TimeSpan.FromSeconds(3)),
                Now,
                "raw://req/1",
                "raw://resp/1")
            : new ProviderResult(
                outcome,
                finality,
                new ProviderTransportResult(status, sent, null, null, null),
                code,
                null,
                null,
                References(full: false),
                new Dictionary<string, JsonElement>(),
                null,
                Now,
                null,
                null);
    }

    public static ProviderCallback Callback() =>
        new(ProviderId, "CB-1", OriginalId, "PRV-REF-77", Result(ProviderOutcome.Success), new CorrelationContext(OriginalId, "corr-cb", "trace-cb"), Now);

    private static readonly JsonSerializerOptions CanonicalOptions = new() { Converters = { new SortedSetConverter() } };

    /// <summary>Order-insensitive (object keys, sets) JSON of a contract object, for structural equality.</summary>
    public static string Canonical(object value)
    {
        var node = JsonSerializer.SerializeToNode(value, value.GetType(), CanonicalOptions);
        return Sort(node)?.ToJsonString() ?? "null";
    }

    private static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Sort(p.Value?.DeepClone())))),
        JsonArray array => new JsonArray([.. array.Select(item => Sort(item?.DeepClone()))]),
        _ => node?.DeepClone(),
    };

    private sealed class SortedSetConverter : JsonConverter<IReadOnlySet<string>>
    {
        public override IReadOnlySet<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, IReadOnlySet<string> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var item in value.Order(StringComparer.Ordinal))
            {
                writer.WriteStringValue(item);
            }

            writer.WriteEndArray();
        }
    }
}
