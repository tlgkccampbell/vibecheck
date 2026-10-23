namespace Orchard.Shared.Kernel;

/// <summary>An error with a code and a message.</summary>
public sealed record Error(string Code, string Message)
{
    public bool IsTransient => Code.StartsWith("transient.", StringComparison.Ordinal);
}

/// <summary>The outcome of an operation: a value or an error.</summary>
public readonly struct Result<T>
{
    private readonly T? value;

    private Result(T? value, Error? error)
    {
        this.value = value;
        Error = error;
    }

    public Error? Error { get; }

    public bool IsSuccess => Error is null;

    public bool IsFailure => Error is not null;

    public T Value => IsSuccess ? value! : throw new InvalidOperationException("The result is a failure.");

    public static Result<T> Success(T value) => new(value, null);

    public static Result<T> Failure(Error error) => new(default, error);

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure(error);
}
