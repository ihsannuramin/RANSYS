namespace Ransys.Domain.Common;

/// <summary>
/// Outcome of an operation that has no value: success, or a controlled <see cref="RansysError"/>.
/// </summary>
public readonly struct Result
{
    private readonly RansysError? _error;

    private Result(RansysError? error) => _error = error;

    public bool IsSuccess => _error is null;

    public bool IsFailure => _error is not null;

    public RansysError Error =>
        _error ?? throw new InvalidOperationException("Cannot read Error of a successful result.");

    public static Result Success() => default;

    public static Result Failure(RansysError error) =>
        new(error ?? throw new ArgumentNullException(nameof(error)));

    public static implicit operator Result(RansysError error) => Failure(error);

    public override string ToString() => IsSuccess ? "Success" : $"Failure({_error})";
}

/// <summary>
/// Outcome of an operation producing <typeparamref name="T"/>: a value, or a controlled <see cref="RansysError"/>.
/// </summary>
public readonly struct Result<T>
{
    private readonly T? _value;
    private readonly RansysError? _error;
    private readonly bool _isSuccess;

    private Result(T value)
    {
        _value = value;
        _error = null;
        _isSuccess = true;
    }

    private Result(RansysError error)
    {
        _value = default;
        _error = error;
        _isSuccess = false;
    }

    public bool IsSuccess => _isSuccess;

    public bool IsFailure => !_isSuccess;

    public T Value => _isSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot read Value of a failed result: {_error}.");

    public RansysError Error => _isSuccess
        ? throw new InvalidOperationException("Cannot read Error of a successful result.")
        : _error ?? throw new InvalidOperationException("Result was not initialized.");

    public static Result<T> Success(T value) => new(value);

    public static Result<T> Failure(RansysError error) =>
        new(error ?? throw new ArgumentNullException(nameof(error)));

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(RansysError error) => Failure(error);

    public override string ToString() => _isSuccess ? $"Success({_value})" : $"Failure({_error})";
}
