using Tooltail.Domain.Identifiers;

namespace Tooltail.Adapters.AgentEvents.Codex;

public enum CodexExecRunStatus
{
    Completed,
    AdapterRejected,
    Cancelled,
    TimedOut,
    LaunchFailed,
    ProcessFailed,
}

public sealed record CodexExecRunResult(
    CodexExecRunStatus Status,
    string ReasonCode,
    RunId RunId,
    int? ExitCode,
    long StandardErrorByteCount,
    bool StandardErrorLimitExceeded,
    CodexEventStreamResult EventStream)
{
    public bool IsSuccess => Status == CodexExecRunStatus.Completed;
}

internal enum BoundedDiscardStatus
{
    Completed,
    LimitExceeded,
    IoFailure,
}

internal sealed record BoundedDiscardResult(
    BoundedDiscardStatus Status,
    long ByteCount);

internal enum PromptWriteStatus
{
    Completed,
    Cancelled,
    IoFailure,
}
