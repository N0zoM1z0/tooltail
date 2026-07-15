using Tooltail.Domain.Execution;

namespace Tooltail.Application.Abstractions;

/// <summary>
/// Persists execution boundaries before returning. Implementations must make journal events
/// append-only and must atomically reject reuse of a consumed approval for another execution.
/// </summary>
public interface IExecutionJournalStore
{
    ValueTask<JournalWriteResult> CreateAsync(
        ExecutionJournal journal,
        PlanApproval consumedApproval,
        CancellationToken cancellationToken = default);

    ValueTask<JournalWriteResult> AppendAsync(
        ExecutionJournalEvent journalEvent,
        CancellationToken cancellationToken = default);

    ValueTask<JournalWriteResult> StoreReceiptAsync(
        ExecutionReceipt receipt,
        CancellationToken cancellationToken = default);
}

public sealed record JournalWriteResult
{
    private JournalWriteResult(bool isSuccess, string? failureCode)
    {
        IsSuccess = isSuccess;
        FailureCode = failureCode;
    }

    public bool IsSuccess { get; }

    public string? FailureCode { get; }

    public static JournalWriteResult Success { get; } = new(true, null);

    public static JournalWriteResult Failure(string failureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        return new JournalWriteResult(false, failureCode);
    }
}
