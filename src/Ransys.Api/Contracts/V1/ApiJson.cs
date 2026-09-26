using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ransys.Api.Contracts.V1;

/// <summary>Marks a DTO property that the OpenAPI schema lists in <c>required</c> (checked by the contract tests).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SchemaRequiredAttribute : Attribute;

/// <summary>
/// The only JSON settings of the public API: camelCase names matched case-sensitively, unknown properties rejected
/// (<c>additionalProperties: false</c>), duplicate properties rejected, numbers never read from strings, no comments or
/// trailing commas, nulls omitted on output.
/// </summary>
public static class ApiJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static void Apply(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PropertyNameCaseInsensitive = false;
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.AllowDuplicateProperties = false;
        options.NumberHandling = JsonNumberHandling.Strict;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.ReadCommentHandling = JsonCommentHandling.Disallow;
        options.AllowTrailingCommas = false;
        options.MaxDepth = 32;
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General);
        Apply(options);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
