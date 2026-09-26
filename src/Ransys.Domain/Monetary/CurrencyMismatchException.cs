namespace Ransys.Domain.Monetary;

/// <summary>
/// Thrown when two <see cref="Money"/> values with different currency definitions are combined or compared.
/// This is a programming error: inputs are validated against one currency definition before arithmetic,
/// and RANSYS never converts implicitly (Canonical Data Model §13–14).
/// </summary>
public sealed class CurrencyMismatchException : InvalidOperationException
{
    public CurrencyMismatchException()
    {
    }

    public CurrencyMismatchException(string message)
        : base(message)
    {
    }

    public CurrencyMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CurrencyMismatchException(CurrencyDefinition left, CurrencyDefinition right)
        : base($"Currency definition mismatch: {left} vs {right}. Explicit conversion is required.")
    {
    }
}
