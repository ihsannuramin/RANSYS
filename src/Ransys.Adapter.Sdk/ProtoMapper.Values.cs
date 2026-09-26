using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Google.Protobuf.WellKnownTypes;

namespace Ransys.Adapter.Sdk;

/// <summary>Scalar and JSON conversions used by <see cref="ProtoMapper"/>.</summary>
public static partial class ProtoMapper
{
    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyJson = new Dictionary<string, JsonElement>();

    /// <summary>
    /// Formats a money amount for the wire: invariant culture, no exponent, no grouping, and the decimal's own
    /// scale is kept ("100.00" stays "100.00").
    /// </summary>
    public static string FormatMoney(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses a wire money amount. Accepts only <c>-?(0|[1-9][0-9]*)(\.[0-9]+)?</c> and rejects anything that
    /// <see cref="decimal"/> would round or overflow, so a value is never silently changed.
    /// </summary>
    public static decimal ParseMoney(string? value, string field = "money.value")
    {
        if (string.IsNullOrEmpty(value) || !MoneyPattern().IsMatch(value))
        {
            throw new ProtoMappingException($"{field} is not a decimal amount string.");
        }

        if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
            || !string.Equals(parsed.ToString(CultureInfo.InvariantCulture), value, StringComparison.Ordinal))
        {
            throw new ProtoMappingException($"{field} cannot be represented exactly as a decimal.");
        }

        return parsed;
    }

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex MoneyPattern();

    private static string FormatGuid(Guid value, string field)
    {
        if (value == Guid.Empty)
        {
            throw new ProtoMappingException($"{field} must not be the empty GUID.");
        }

        return value.ToString("D");
    }

    private static Guid ParseGuid(string? value, string field)
    {
        if (!Guid.TryParseExact(value, "D", out var parsed) || parsed == Guid.Empty)
        {
            throw new ProtoMappingException($"{field} is not a non-empty GUID in 'D' format.");
        }

        return parsed;
    }

    private static Guid? ParseOptionalGuid(bool has, string value, string field) =>
        has ? ParseGuid(value, field) : null;

    private static T Required<T>(T? value, string field)
        where T : class =>
        value ?? throw new ProtoMappingException($"{field} is required.");

    private static string RequiredText(string? value, string field) =>
        string.IsNullOrEmpty(value) ? throw new ProtoMappingException($"{field} is required.") : value;

    private static Timestamp ToTimestamp(DateTimeOffset value) => Timestamp.FromDateTimeOffset(value);

    private static DateTimeOffset FromTimestamp(Timestamp? value, string field)
    {
        var timestamp = Required(value, field);
        try
        {
            return timestamp.ToDateTimeOffset();
        }
        catch (InvalidOperationException ex)
        {
            throw new ProtoMappingException($"{field} is not a valid timestamp.", ex);
        }
    }

    private static Duration ToDuration(TimeSpan value) => Duration.FromTimeSpan(value);

    private static Duration? ToOptionalDuration(TimeSpan? value) => value is { } v ? Duration.FromTimeSpan(v) : null;

    private static TimeSpan FromDuration(Duration? value, string field) =>
        FromOptionalDuration(Required(value, field), field)!.Value;

    private static TimeSpan? FromOptionalDuration(Duration? value, string field)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return value.ToTimeSpan();
        }
        catch (InvalidOperationException ex)
        {
            throw new ProtoMappingException($"{field} is not a valid duration.", ex);
        }
    }

    // ---- google.protobuf.Struct <-> IReadOnlyDictionary<string, JsonElement> --------------------------------

    /// <summary>
    /// Converts a JSON dictionary to a Struct. Struct numbers are IEEE doubles, so a JSON number is accepted only
    /// if it survives the double round trip unchanged; carry money in <c>Data</c>/<c>Metadata</c> as strings.
    /// </summary>
    private static Struct ToStruct(IReadOnlyDictionary<string, JsonElement>? values, string field)
    {
        var result = new Struct();
        if (values is null)
        {
            return result;
        }

        foreach (var (key, element) in values)
        {
            result.Fields.Add(key, ToValue(element, $"{field}.{key}"));
        }

        return result;
    }

    private static IReadOnlyDictionary<string, JsonElement> FromStruct(Struct? value, string field)
    {
        if (value is null || value.Fields.Count == 0)
        {
            return EmptyJson;
        }

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, item) in value.Fields)
        {
            result.Add(key, ToJsonElement(item, $"{field}.{key}"));
        }

        return result;
    }

    private static Struct ObjectToStruct(JsonElement element, string field)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ProtoMappingException($"{field} must be a JSON object.");
        }

        var result = new Struct();
        foreach (var property in element.EnumerateObject())
        {
            result.Fields.Add(property.Name, ToValue(property.Value, $"{field}.{property.Name}"));
        }

        return result;
    }

    private static JsonElement StructToObject(Struct value, string field) =>
        ToJsonElement(Value.ForStruct(value), field);

    private static Value ToValue(JsonElement element, string field)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                return Value.ForStruct(ObjectToStruct(element, field));
            case JsonValueKind.Array:
                var items = new List<Value>();
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    items.Add(ToValue(item, $"{field}[{index++}]"));
                }

                return Value.ForList([.. items]);
            case JsonValueKind.String:
                return Value.ForString(element.GetString()!);
            case JsonValueKind.Number:
                return Value.ForNumber(ToSafeDouble(element.GetRawText(), field));
            case JsonValueKind.True:
                return Value.ForBool(true);
            case JsonValueKind.False:
                return Value.ForBool(false);
            case JsonValueKind.Null:
                return Value.ForNull();
            default:
                throw new ProtoMappingException($"{field} is not a JSON value.");
        }
    }

    private static double ToSafeDouble(string raw, string field)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            || !double.IsFinite(number)
            || !string.Equals(CanonicalNumber(raw), CanonicalNumber(FormatDouble(number)), StringComparison.Ordinal))
        {
            throw new ProtoMappingException(
                $"{field} is a JSON number that does not survive the protobuf double round trip; send it as a string.");
        }

        return number;
    }

    private static string FormatDouble(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Canonical text of a JSON number: significant digits and an exponent (e.g. "1.50" and "15e-1" → "15e-1").</summary>
    private static string CanonicalNumber(string raw)
    {
        var text = raw.Trim();
        var negative = text.StartsWith('-');
        if (negative || text.StartsWith('+'))
        {
            text = text[1..];
        }

        var exponent = 0;
        var e = text.IndexOfAny(['e', 'E']);
        if (e >= 0)
        {
            exponent = int.Parse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..e];
        }

        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            exponent -= text.Length - dot - 1;
            text = text.Remove(dot, 1);
        }

        text = text.TrimStart('0');
        var trimmed = text.TrimEnd('0');
        exponent += text.Length - trimmed.Length;

        return trimmed.Length == 0
            ? "0"
            : string.Create(CultureInfo.InvariantCulture, $"{(negative ? "-" : string.Empty)}{trimmed}e{exponent}");
    }

    private static JsonElement ToJsonElement(Value value, string field)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteValue(writer, value, field);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WriteValue(Utf8JsonWriter writer, Value value, string field)
    {
        switch (value.KindCase)
        {
            case Value.KindOneofCase.NullValue:
                writer.WriteNullValue();
                break;
            case Value.KindOneofCase.NumberValue:
                if (!double.IsFinite(value.NumberValue))
                {
                    throw new ProtoMappingException($"{field} is not a finite number.");
                }

                writer.WriteRawValue(FormatDouble(value.NumberValue));
                break;
            case Value.KindOneofCase.StringValue:
                writer.WriteStringValue(value.StringValue);
                break;
            case Value.KindOneofCase.BoolValue:
                writer.WriteBooleanValue(value.BoolValue);
                break;
            case Value.KindOneofCase.StructValue:
                writer.WriteStartObject();
                foreach (var (key, item) in value.StructValue.Fields)
                {
                    writer.WritePropertyName(key);
                    WriteValue(writer, item, $"{field}.{key}");
                }

                writer.WriteEndObject();
                break;
            case Value.KindOneofCase.ListValue:
                writer.WriteStartArray();
                var index = 0;
                foreach (var item in value.ListValue.Values)
                {
                    WriteValue(writer, item, $"{field}[{index++}]");
                }

                writer.WriteEndArray();
                break;
            default:
                throw new ProtoMappingException($"{field} has no value kind.");
        }
    }

    private static JsonElement JsonString(string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value));
        return document.RootElement.Clone();
    }

    private static string JsonText(JsonElement element, string field) =>
        element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : throw new ProtoMappingException($"{field} must be a JSON string.");
}
