namespace Tooltail.Contracts.Json;

public sealed record ContractParseError(string Code, string Message);

public readonly record struct ContractParseResult<T>
    where T : class
{
    internal ContractParseResult(T? value, ContractParseError? error, bool isSuccess)
    {
        Value = value;
        Error = error;
        IsSuccess = isSuccess;
    }

    public bool IsSuccess { get; }

    public T? Value { get; }

    public ContractParseError? Error { get; }
}

public static class ContractParseResult
{
    public static ContractParseResult<T> Success<T>(T value)
        where T : class =>
        new(value, null, isSuccess: true);

    public static ContractParseResult<T> Failure<T>(string code, string message)
        where T : class =>
        new(null, new ContractParseError(code, message), isSuccess: false);
}
