using System.Text.Json;
using Dapper;
using Ransys.Domain.Common;

namespace Ransys.Persistence.PostgreSql;

/// <summary>Conversions between domain values and PostgreSQL column values.</summary>
internal static class DbValues
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    static DbValues()
    {
        // Rows map snake_case columns to PascalCase properties.
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    /// <summary>Forces the static initializer (Dapper naming convention) to run before any query.</summary>
    public static void EnsureConfigured()
    {
    }

    /// <summary>
    /// <c>timestamptz</c> stores an instant. Npgsql only writes UTC values, so every timestamp is sent as UTC.
    /// </summary>
    public static DateTime ToDb(DateTimeOffset value) => value.UtcDateTime;

    public static DateTime? ToDb(DateTimeOffset? value) => value?.UtcDateTime;

    public static DateTimeOffset FromDb(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public static DateTimeOffset? FromDb(DateTime? value) => value is { } v ? FromDb(v) : null;

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    public static T FromJson<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException($"JSON column could not be read as {typeof(T).Name}.");

    public static string MetadataToJson(ExtensionMetadata metadata) =>
        JsonSerializer.Serialize(metadata.Values, JsonOptions);

    public static Result<ExtensionMetadata> MetadataFromJson(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? ExtensionMetadata.Empty
            : ExtensionMetadata.Create(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOptions));
}
