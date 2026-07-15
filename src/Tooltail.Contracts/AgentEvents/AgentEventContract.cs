using System.Text.Json.Serialization;
using Tooltail.Contracts.Json;

namespace Tooltail.Contracts.AgentEvents;

public sealed record AgentEventContract : IVersionedContract
{
    public required string SchemaVersion { get; init; }

    public required Guid EventId { get; init; }

    public required Guid RunId { get; init; }

    public required long Sequence { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required AgentEventSource Source { get; init; }

    public required AgentEventType Type { get; init; }

    public required AgentEventSeverity Severity { get; init; }

    public required AgentEventDataContract Data { get; init; }
}

public enum AgentEventSource
{
    Simulator,

    [JsonStringEnumMemberName("generic-jsonl")]
    GenericJsonl,

    [JsonStringEnumMemberName("codex-exec-jsonl")]
    CodexExecJsonl,
}

public enum AgentEventType
{
    RunStarted,
    RunCompleted,
    RunFailed,
    RunCancelled,
    RunPaused,
    RunResumed,
    RunBlocked,
    ObservationStarted,
    ObservationStopped,
    ToolStarted,
    ToolCompleted,
    ToolFailed,
    InputRequired,
    InputResolved,
    SubagentStarted,
    SubagentCompleted,
    PermissionRevoked,
    Heartbeat,
    AdapterDisconnected,
}

public enum AgentEventSeverity
{
    Trace,
    Info,
    Warning,
    Error,
}

public sealed record AgentEventDataContract
{
    public AgentToolKind? ToolKind { get; init; }

    public string? ToolCallId { get; init; }

    public string? QuestionId { get; init; }

    public string? SubagentId { get; init; }

    public string? DisplayLabel { get; init; }

    public string? StatusCode { get; init; }

    public decimal? Progress { get; init; }

    public int? ParallelUnitCount { get; init; }
}

public enum AgentToolKind
{
    Browser,
    Code,
    Email,
    File,
    Terminal,
    Other,
}
