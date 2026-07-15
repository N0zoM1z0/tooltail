namespace Tooltail.Domain.Common;

public readonly record struct DomainResult<T>
{
    internal DomainResult(T? value, DomainError? error, bool isSuccess)
    {
        Value = value;
        Error = error;
        IsSuccess = isSuccess;
    }

    public bool IsSuccess { get; }

    public T? Value { get; }

    public DomainError? Error { get; }
}

public static class DomainResult
{
    public static DomainResult<T> Success<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new DomainResult<T>(value, null, isSuccess: true);
    }

    public static DomainResult<T> Failure<T>(string code, string message) =>
        new(default, new DomainError(code, message), isSuccess: false);
}
