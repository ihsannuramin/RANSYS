namespace Ransys.Domain.Common;

/// <summary>
/// String validation helpers. Values are validated, never silently trimmed or truncated
/// (Canonical Data Model §108–109).
/// </summary>
internal static class Text
{
    public static RansysError? Required(string? value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return RansysError.Validation(ErrorCodes.Required, $"{field} is required.", field);
        }

        return MaxLength(value, field, maxLength);
    }

    /// <summary>Null is allowed; a present value must not be blank and must fit <paramref name="maxLength"/>.</summary>
    public static RansysError? Optional(string? value, string field, int maxLength)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return RansysError.Validation(ErrorCodes.Required, $"{field} must not be blank when present.", field);
        }

        return MaxLength(value, field, maxLength);
    }

    /// <summary>Null is allowed; a present value must not be blank. No length limit is defined.</summary>
    public static RansysError? OptionalNotBlank(string? value, string field) =>
        value is not null && string.IsNullOrWhiteSpace(value)
            ? RansysError.Validation(ErrorCodes.Required, $"{field} must not be blank when present.", field)
            : null;

    public static RansysError? RequiredNotBlank(string? value, string field) =>
        string.IsNullOrWhiteSpace(value)
            ? RansysError.Validation(ErrorCodes.Required, $"{field} is required.", field)
            : null;

    private static RansysError? MaxLength(string value, string field, int maxLength) =>
        value.Length > maxLength
            ? RansysError.Validation(ErrorCodes.TooLong, $"{field} exceeds {maxLength} characters.", field)
            : null;

    /// <summary>Returns the first non-null error.</summary>
    public static RansysError? FirstError(params RansysError?[] errors) =>
        errors.FirstOrDefault(e => e is not null);
}
