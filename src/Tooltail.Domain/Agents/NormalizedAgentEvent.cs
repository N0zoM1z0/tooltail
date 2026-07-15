using Tooltail.Domain.Common;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Domain.Agents;

public enum NormalizedAgentEventSource
{
    Simulator,
    GenericJsonl,
    CodexExecJsonl,
}

public enum NormalizedAgentEventType
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

public enum NormalizedAgentEventSeverity
{
    Trace,
    Info,
    Warning,
    Error,
}

public enum NormalizedAgentToolKind
{
    Browser,
    Code,
    Email,
    File,
    Terminal,
    Other,
}

public sealed record NormalizedAgentEventData
{
    private NormalizedAgentEventData(
        NormalizedAgentToolKind? toolKind,
        string? toolCallId,
        string? questionId,
        string? subagentId,
        string? displayLabel,
        string? statusCode,
        decimal? progress,
        int? parallelUnitCount)
    {
        ToolKind = toolKind;
        ToolCallId = toolCallId;
        QuestionId = questionId;
        SubagentId = subagentId;
        DisplayLabel = displayLabel;
        StatusCode = statusCode;
        Progress = progress;
        ParallelUnitCount = parallelUnitCount;
    }

    public NormalizedAgentToolKind? ToolKind { get; }

    public string? ToolCallId { get; }

    public string? QuestionId { get; }

    public string? SubagentId { get; }

    public string? DisplayLabel { get; }

    public string? StatusCode { get; }

    public decimal? Progress { get; }

    public int? ParallelUnitCount { get; }

    public static DomainResult<NormalizedAgentEventData> Create(
        NormalizedAgentToolKind? toolKind = null,
        string? toolCallId = null,
        string? questionId = null,
        string? subagentId = null,
        string? displayLabel = null,
        string? statusCode = null,
        decimal? progress = null,
        int? parallelUnitCount = null)
    {
        if (toolKind is not null && !Enum.IsDefined(toolKind.Value))
        {
            return Failure("agent_event.tool_kind_unknown", "The normalized tool kind is unknown.");
        }

        if (!IsOpaqueId(toolCallId) || !IsOpaqueId(questionId) || !IsOpaqueId(subagentId))
        {
            return Failure("agent_event.opaque_id_invalid", "An opaque event identifier has an invalid shape.");
        }

        if (displayLabel is not null &&
            (displayLabel.Length > 80 || displayLabel.Any(char.IsControl)))
        {
            return Failure(
                "agent_event.display_label_invalid",
                "Untrusted display text exceeds its bound or contains control characters.");
        }

        if (statusCode is not null && !IsStatusCode(statusCode))
        {
            return Failure("agent_event.status_code_invalid", "The event status code has an invalid shape.");
        }

        if (progress is < 0 or > 1)
        {
            return Failure("agent_event.progress_out_of_range", "Event progress must be between zero and one.");
        }

        if (parallelUnitCount is < 0 or > 32)
        {
            return Failure(
                "agent_event.parallel_count_out_of_range",
                "The parallel unit count exceeds its bound.");
        }

        return DomainResult.Success(
            new NormalizedAgentEventData(
                toolKind,
                toolCallId,
                questionId,
                subagentId,
                displayLabel,
                statusCode,
                progress,
                parallelUnitCount));
    }

    private static bool IsOpaqueId(string? value) =>
        value is null ||
        (value.Length is >= 1 and <= 96 && value.All(static character =>
            character is >= 'A' and <= 'Z' or
                >= 'a' and <= 'z' or
                >= '0' and <= '9' or
                '.' or '_' or ':' or '-'));

    private static bool IsStatusCode(string value) =>
        value.Length is >= 1 and <= 64 &&
        value[0] is >= 'a' and <= 'z' &&
        value.All(static character =>
            character is >= 'a' and <= 'z' or
                >= '0' and <= '9' or
                '_' or '.' or '-');

    private static DomainResult<NormalizedAgentEventData> Failure(string code, string message) =>
        DomainResult.Failure<NormalizedAgentEventData>(code, message);
}

public sealed record NormalizedAgentEvent
{
    private NormalizedAgentEvent(
        AgentEventId id,
        RunId runId,
        long sequence,
        DateTimeOffset occurredUtc,
        NormalizedAgentEventSource source,
        NormalizedAgentEventType type,
        NormalizedAgentEventSeverity severity,
        NormalizedAgentEventData data)
    {
        Id = id;
        RunId = runId;
        Sequence = sequence;
        OccurredUtc = occurredUtc;
        Source = source;
        Type = type;
        Severity = severity;
        Data = data;
    }

    public AgentEventId Id { get; }

    public RunId RunId { get; }

    public long Sequence { get; }

    public DateTimeOffset OccurredUtc { get; }

    public NormalizedAgentEventSource Source { get; }

    public NormalizedAgentEventType Type { get; }

    public NormalizedAgentEventSeverity Severity { get; }

    public NormalizedAgentEventData Data { get; }

    public static DomainResult<NormalizedAgentEvent> Create(
        AgentEventId id,
        RunId runId,
        long sequence,
        DateTimeOffset occurredUtc,
        NormalizedAgentEventSource source,
        NormalizedAgentEventType type,
        NormalizedAgentEventSeverity severity,
        NormalizedAgentEventData data)
    {
        if (id.Value == Guid.Empty || runId.Value == Guid.Empty)
        {
            return Failure("agent_event.identity_empty", "Normalized event identities cannot be empty.");
        }

        if (sequence < 0)
        {
            return Failure("agent_event.sequence_negative", "Normalized event sequence cannot be negative.");
        }

        if (occurredUtc.Offset != TimeSpan.Zero)
        {
            return Failure("agent_event.time_not_utc", "Normalized event time must use UTC.");
        }

        if (!Enum.IsDefined(source) || !Enum.IsDefined(type) || !Enum.IsDefined(severity))
        {
            return Failure("agent_event.enum_unknown", "A normalized event enum is unknown.");
        }

        if (data is null)
        {
            return Failure("agent_event.data_missing", "Normalized event data is required.");
        }

        string? requiredDataError = ValidateRequiredData(type, data);
        return requiredDataError is null
            ? DomainResult.Success(
                new NormalizedAgentEvent(
                    id,
                    runId,
                    sequence,
                    occurredUtc,
                    source,
                    type,
                    severity,
                    data))
            : Failure("agent_event.required_data_missing", requiredDataError);
    }

    private static string? ValidateRequiredData(
        NormalizedAgentEventType type,
        NormalizedAgentEventData data) =>
        type switch
        {
            NormalizedAgentEventType.ToolStarted or
                NormalizedAgentEventType.ToolCompleted or
                NormalizedAgentEventType.ToolFailed when
                data.ToolKind is null || data.ToolCallId is null =>
                "Tool events require a closed tool kind and opaque call ID.",
            NormalizedAgentEventType.InputRequired or
                NormalizedAgentEventType.InputResolved when data.QuestionId is null =>
                "Input events require an opaque question ID.",
            NormalizedAgentEventType.SubagentStarted or
                NormalizedAgentEventType.SubagentCompleted when data.SubagentId is null =>
                "Subagent events require an opaque subagent ID.",
            _ => null,
        };

    private static DomainResult<NormalizedAgentEvent> Failure(string code, string message) =>
        DomainResult.Failure<NormalizedAgentEvent>(code, message);
}
