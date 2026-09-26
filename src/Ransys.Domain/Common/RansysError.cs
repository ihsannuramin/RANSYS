namespace Ransys.Domain.Common;

/// <summary>
/// Error categories from Canonical Data Model §71.
/// </summary>
public enum ErrorCategory
{
    Validation,
    Authentication,
    Authorization,
    Financial,
    Routing,
    Provider,
    Infrastructure,
    Conflict,
    Internal,
}

/// <summary>
/// Controlled business/domain failure (Canonical Data Model §71).
/// Expected outcomes such as INSUFFICIENT_BALANCE are returned as <see cref="RansysError"/>,
/// never thrown (Canonical Data Model §72, main.md §25).
/// </summary>
public sealed record RansysError(
    string Code,
    ErrorCategory Category,
    string Message,
    string? Field = null,
    bool Retryable = false)
{
    public static RansysError Validation(string code, string message, string? field = null) =>
        new(code, ErrorCategory.Validation, message, field);

    public static RansysError Conflict(string code, string message) =>
        new(code, ErrorCategory.Conflict, message);

    public static RansysError Financial(string code, string message) =>
        new(code, ErrorCategory.Financial, message);

    public override string ToString() =>
        Field is null ? $"{Code}: {Message}" : $"{Code} ({Field}): {Message}";
}
