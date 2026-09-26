using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using Ransys.Api.Contracts.V1;
using Ransys.Api.Mapping;
using Ransys.Api.Validation;
using Ransys.Domain;
using Ransys.Domain.Transactions;
using Ransys.Testing.PostgreSql;
using static Ransys.Api.Tests.ApiHarness;

namespace Ransys.Api.Tests;

/// <summary>
/// Handoff §29: the OpenAPI file stays valid, the DTOs equal its schemas (property names and required sets), the endpoint
/// set equals its paths, and real API output conforms to the response schemas.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ContractTests(PostgresDatabaseFixture db, ApiFactory factory)
{
    private static readonly Lazy<OpenApiDocument> Document = new(LoadDocument);

    public static TheoryData<string, Type> SchemaTypes => new()
    {
        { "MoneyInput", typeof(MoneyInputDto) },
        { "CustomerInput", typeof(CustomerInputDto) },
        { "EndpointInput", typeof(EndpointInputDto) },
        { "InquiryRequest", typeof(InquiryRequest) },
        { "PaymentRequest", typeof(PaymentRequest) },
        { "TransferRequest", typeof(TransferRequest) },
        { "RefundRequest", typeof(RefundRequest) },
        { "ReversalRequest", typeof(ReversalRequest) },
        { "VoidRequest", typeof(VoidRequest) },
        { "PublicReferences", typeof(PublicReferencesDto) },
        { "TransactionResponse", typeof(TransactionResponse) },
        { "TransactionDetailResponse", typeof(TransactionDetailResponse) },
        { "ApiErrorResponse", typeof(ApiErrorResponse) },
    };

    private readonly ApiHarness _h = new(db, factory);

    [Fact]
    public void OpenApi_document_parses_as_openapi_3_1_without_errors()
    {
        var (document, diagnostic) = Read();

        Assert.Empty(diagnostic.Errors);
        Assert.Equal(OpenApiSpecVersion.OpenApi3_1, diagnostic.SpecificationVersion);
        Assert.Equal("1.0.0", document.Info.Version);
    }

    [Theory]
    [MemberData(nameof(SchemaTypes))]
    public void Dto_properties_and_required_set_equal_the_schema(string schemaName, Type dto)
    {
        var schema = Schema(schemaName);
        var properties = JsonProperties(dto);

        Assert.Equal(schema.Properties!.Keys.Order(StringComparer.Ordinal), properties.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(
            (schema.Required ?? new HashSet<string>()).Order(StringComparer.Ordinal),
            properties.Where(p => p.Value.GetCustomAttribute<SchemaRequiredAttribute>() is not null).Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.False(schema.AdditionalPropertiesAllowed, $"{schemaName} must declare additionalProperties: false");
    }

    [Theory]
    [MemberData(nameof(SchemaTypes))]
    public void Dtos_use_only_public_contract_types(string schemaName, Type dto)
    {
        // Handoff §9: no domain, persistence, ledger or provider types in the public contract.
        var allowed = new[] { typeof(string), typeof(DateTimeOffset), typeof(DateTimeOffset?), typeof(Dictionary<string, JsonElement>), typeof(IReadOnlyDictionary<string, JsonElement>) };
        Assert.All(dto.GetProperties(), p => Assert.True(
            allowed.Contains(p.PropertyType) || p.PropertyType.Namespace == typeof(ApiJson).Namespace,
            $"{schemaName}.{p.Name} has non-contract type {p.PropertyType}"));
    }

    [Fact]
    public void Every_closed_object_schema_has_a_dto()
    {
        var closed = Document.Value.Components!.Schemas!
            .Where(s => s.Value.Type == JsonSchemaType.Object && !s.Value.AdditionalPropertiesAllowed)
            .Select(s => s.Key)
            .Order(StringComparer.Ordinal);

        Assert.Equal(closed, SchemaTypes.Select(row => (string)row[0]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Validation_patterns_and_enums_match_the_schema()
    {
        Assert.Equal(Schema("DecimalAmount").Pattern, Dollar(RequestValidator.DecimalAmountRegex));
        Assert.Equal(Schema("MoneyInput").Properties!["currency"].Pattern, Dollar(RequestValidator.CurrencyRegex));
        Assert.Equal(Enum(Schema("EndpointInput").Properties!["type"]), RequestValidator.EndpointTypes.Order(StringComparer.Ordinal));
        Assert.Equal(Enum(Schema("EndpointInput").Properties!["type"]), CanonicalCodes.EndpointType.Codes.Order(StringComparer.Ordinal));
        Assert.Equal(Enum(Schema("FinancialStatus")), CanonicalCodes.FinancialStatus.Codes.Order(StringComparer.Ordinal));
        Assert.Equal(Enum(Schema("ReconciliationStatus")), CanonicalCodes.ReconciliationStatus.Codes.Order(StringComparer.Ordinal));
        Assert.Equal(Enum(Schema("SettlementStatus")), CanonicalCodes.SettlementStatus.Codes.Order(StringComparer.Ordinal));
        var statuses = Enum(Schema("TransactionStatus"));
        Assert.All(System.Enum.GetValues<ProcessingStatus>(), s => Assert.Contains(ResponseMapper.TransactionStatus(s), statuses));
        Assert.Equal(128, Document.Value.Components!.Parameters!["IdempotencyKey"].Schema!.MaxLength);
        Assert.Equal(RequestValidator.MaxIdempotencyKeyLength, Document.Value.Components!.Parameters!["IdempotencyKey"].Schema!.MaxLength);
    }

    [Fact]
    public void Mapped_endpoints_equal_the_openapi_paths()
    {
        var expected = Document.Value.Paths!
            .SelectMany(p => p.Value.Operations!.Keys.Select(m => $"{m.Method.ToUpperInvariant()} {p.Key}"))
            .Order(StringComparer.Ordinal);
        var actual = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/", StringComparison.Ordinal))
            .SelectMany(e => e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Select(m => $"{m} {e.RoutePattern.RawText}"))
            .Order(StringComparer.Ordinal);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Real_responses_conform_to_the_response_schemas()
    {
        var s = await _h.NewScenario();
        s.Adapter.ThenSuccess(rrn: "RRN-1");

        var payment = await _h.Post(s.Channel, "/api/v1/payments", Payment(s, merchantReference: "INV-1"));
        var paymentJson = await Json(payment);
        var detail = await Json(await _h.Get(s.Channel, $"/api/v1/transactions/{paymentJson.GetProperty("ransysTransactionId").GetString()}"));
        var reversal = await Json(await _h.Post(s.Channel, "/api/v1/reversals", new
        {
            clientReference = NewReference(),
            originalTransactionId = paymentJson.GetProperty("ransysTransactionId").GetString(),
            reason = "r",
            requestTimestamp = Now(),
        }));
        var reversalDetail = await Json(await _h.Get(s.Channel, $"/api/v1/transactions/{reversal.GetProperty("ransysTransactionId").GetString()}"));
        var error = await _h.Post(s.Channel, "/api/v1/payments", new { });

        Assert.Equal(HttpStatusCode.OK, payment.StatusCode);
        Conforms("TransactionResponse", paymentJson);
        Conforms("TransactionResponse", reversal);
        Conforms("TransactionDetailResponse", detail);
        Conforms("TransactionDetailResponse", reversalDetail);
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Conforms("ApiErrorResponse", await Json(error));
    }

    [Fact]
    public void Schema_check_detects_undeclared_properties_bad_enums_and_missing_required_fields()
    {
        const string valid = """{"ransysTransactionId":"0199aabb-ccdd-7eef-8123-0123456789ab","clientReference":"C","responseCode":"0000","responseMessage":"Success","transactionStatus":"SUCCESS","references":{"rrn":"1"},"data":{},"timestamp":"2026-09-27T10:15:31+07:00"}""";

        Conforms("TransactionResponse", JsonDocument.Parse(valid).RootElement);
        Assert.ThrowsAny<Exception>(() => Conforms("TransactionResponse", JsonDocument.Parse(valid.Replace("\"data\":{}", "\"data\":{},\"providerId\":\"x\"", StringComparison.Ordinal)).RootElement));
        Assert.ThrowsAny<Exception>(() => Conforms("TransactionResponse", JsonDocument.Parse(valid.Replace("\"SUCCESS\"", "\"REVERSAL_PENDING\"", StringComparison.Ordinal)).RootElement));
        Assert.ThrowsAny<Exception>(() => Conforms("TransactionResponse", JsonDocument.Parse(valid.Replace("\"0000\"", "\"00\"", StringComparison.Ordinal)).RootElement));
        Assert.ThrowsAny<Exception>(() => Conforms("TransactionResponse", JsonDocument.Parse(valid.Replace("\"references\":{\"rrn\":\"1\"}", "\"references\":{\"postingKey\":\"1\"}", StringComparison.Ordinal)).RootElement));
        Assert.ThrowsAny<Exception>(() => Conforms("TransactionResponse", JsonDocument.Parse(valid.Replace(",\"data\":{}", "", StringComparison.Ordinal)).RootElement));
    }

    /// <summary>
    /// Structural validation of a JSON value against a schema of this document: only declared properties
    /// (additionalProperties: false), all required ones, string/object/number kinds, enums, patterns, formats.
    /// </summary>
    private static void Conforms(string schemaName, JsonElement value) => Conforms(schemaName, Schema(schemaName), value);

    private static void Conforms(string path, IOpenApiSchema schema, JsonElement value)
    {
        if (schema.Type == JsonSchemaType.Object || schema.Properties is { Count: > 0 })
        {
            Assert.Equal(JsonValueKind.Object, value.ValueKind);
            var names = value.EnumerateObject().Select(p => p.Name).ToList();
            Assert.All(schema.Required ?? new HashSet<string>(), r => Assert.True(names.Contains(r), $"{path}: missing required '{r}'"));
            if (!schema.AdditionalPropertiesAllowed)
            {
                Assert.All(names, n => Assert.True(schema.Properties!.ContainsKey(n), $"{path}: undeclared property '{n}'"));
            }

            foreach (var property in value.EnumerateObject())
            {
                if (schema.Properties?.TryGetValue(property.Name, out var child) == true)
                {
                    Conforms($"{path}.{property.Name}", child, property.Value);
                }
            }

            return;
        }

        if (schema.Type == JsonSchemaType.String)
        {
            Assert.True(value.ValueKind == JsonValueKind.String, $"{path}: expected a string");
            var text = value.GetString()!;
            if (schema.Enum is { Count: > 0 })
            {
                Assert.Contains(text, Enum(schema));
            }

            if (schema.Pattern is { } pattern)
            {
                Assert.Matches(new Regex(pattern), text);
            }

            switch (schema.Format)
            {
                case "uuid":
                    Assert.True(Guid.TryParseExact(text, "D", out _), $"{path}: not a uuid");
                    break;
                case "date-time":
                    Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$"), text);
                    break;
            }
        }
    }

    private static IOpenApiSchema Schema(string name) => Document.Value.Components!.Schemas![name];

    private static List<string> Enum(IOpenApiSchema schema) =>
        schema.Enum!.Select(e => e!.GetValue<string>()).Order(StringComparer.Ordinal).ToList();

    private static string Dollar(string regex) => regex.EndsWith(@"\z", StringComparison.Ordinal) ? regex[..^2] + "$" : regex;

    private static Dictionary<string, PropertyInfo> JsonProperties(Type dto) =>
        dto.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? throw new InvalidOperationException($"{dto.Name}.{p.Name} has no JsonPropertyName"));

    private static OpenApiDocument LoadDocument() => Read().Document;

    private static (OpenApiDocument Document, OpenApiDiagnostic Diagnostic) Read()
    {
        var settings = new OpenApiReaderSettings();
        settings.AddYamlReader();
        using var stream = new MemoryStream(File.ReadAllBytes(Path.Combine(RepositoryRoot(), "docs", "RANSYS_OpenAPI_v1.yaml")));
        var result = OpenApiDocument.Load(stream, "yaml", settings);
        return (result.Document ?? throw new InvalidOperationException("OpenAPI document did not load."), result.Diagnostic!);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ransys.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate Ransys.sln.");
    }
}
