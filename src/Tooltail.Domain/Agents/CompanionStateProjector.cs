using System.Collections.Frozen;
using Tooltail.Domain.Common;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Domain.Agents;

public enum CompanionBodyState
{
    Idle,
    Working,
    Observing,
    NeedsInput,
    Blocked,
    Paused,
    CompletedUnopened,
    Cancelled,
    Failed,
    PermissionRevoked,
    Disconnected,
}

public enum AgentEventDisposition
{
    Applied,
    DuplicateIgnored,
}

public sealed record AgentEventApplication(
    AgentEventDisposition Disposition,
    AgentRunProjection Projection);

public sealed record AgentRunProjection
{
    internal AgentRunProjection(
        RunId runId,
        long? lastSequence,
        DateTimeOffset? lastOccurredUtc,
        IReadOnlyDictionary<AgentEventId, NormalizedAgentEvent> seenEvents,
        IReadOnlyDictionary<string, NormalizedAgentToolKind> activeTools,
        IEnumerable<string> pendingQuestionIds,
        IEnumerable<string> activeSubagentIds,
        bool hasStarted,
        bool runActive,
        bool observing,
        bool paused,
        bool blocked,
        bool completed,
        bool cancelled,
        bool failed,
        bool permissionRevoked,
        bool disconnected)
    {
        RunId = runId;
        LastSequence = lastSequence;
        LastOccurredUtc = lastOccurredUtc;
        SeenEvents = seenEvents.ToFrozenDictionary();
        ActiveTools = activeTools.ToFrozenDictionary(StringComparer.Ordinal);
        PendingQuestionIds = pendingQuestionIds.ToFrozenSet(StringComparer.Ordinal);
        ActiveSubagentIds = activeSubagentIds.ToFrozenSet(StringComparer.Ordinal);
        HasStarted = hasStarted;
        RunActive = runActive;
        Observing = observing;
        Paused = paused;
        Blocked = blocked;
        Completed = completed;
        Cancelled = cancelled;
        Failed = failed;
        PermissionRevoked = permissionRevoked;
        Disconnected = disconnected;
        BodyState = ProjectBodyState(this);
    }

    public RunId RunId { get; }

    public long? LastSequence { get; }

    public DateTimeOffset? LastOccurredUtc { get; }

    public int SeenEventCount => SeenEvents.Count;

    public IReadOnlyDictionary<string, NormalizedAgentToolKind> ActiveTools { get; }

    public IReadOnlySet<string> PendingQuestionIds { get; }

    public IReadOnlySet<string> ActiveSubagentIds { get; }

    public CompanionBodyState BodyState { get; }

    internal bool HasStarted { get; }

    internal bool RunActive { get; }

    internal bool Observing { get; }

    internal bool Paused { get; }

    internal bool Blocked { get; }

    internal bool Completed { get; }

    internal bool Cancelled { get; }

    internal bool Failed { get; }

    internal bool PermissionRevoked { get; }

    internal bool Disconnected { get; }

    internal IReadOnlyDictionary<AgentEventId, NormalizedAgentEvent> SeenEvents { get; }

    public static AgentRunProjection Empty(RunId runId)
    {
        if (runId.Value == Guid.Empty)
        {
            throw new ArgumentException("Run identity cannot be empty.", nameof(runId));
        }

        return new AgentRunProjection(
            runId,
            null,
            null,
            new Dictionary<AgentEventId, NormalizedAgentEvent>(),
            new Dictionary<string, NormalizedAgentToolKind>(StringComparer.Ordinal),
            [],
            [],
            hasStarted: false,
            runActive: false,
            observing: false,
            paused: false,
            blocked: false,
            completed: false,
            cancelled: false,
            failed: false,
            permissionRevoked: false,
            disconnected: false);
    }

    private static CompanionBodyState ProjectBodyState(AgentRunProjection projection)
    {
        if (projection.Failed)
        {
            return CompanionBodyState.Failed;
        }

        if (projection.PermissionRevoked)
        {
            return CompanionBodyState.PermissionRevoked;
        }

        if (projection.Disconnected)
        {
            return CompanionBodyState.Disconnected;
        }

        if (projection.PendingQuestionIds.Count > 0)
        {
            return CompanionBodyState.NeedsInput;
        }

        if (projection.Blocked)
        {
            return CompanionBodyState.Blocked;
        }

        if (projection.Paused)
        {
            return CompanionBodyState.Paused;
        }

        if (projection.RunActive || projection.ActiveTools.Count > 0 || projection.ActiveSubagentIds.Count > 0)
        {
            return CompanionBodyState.Working;
        }

        if (projection.Observing)
        {
            return CompanionBodyState.Observing;
        }

        if (projection.Completed)
        {
            return CompanionBodyState.CompletedUnopened;
        }

        return projection.Cancelled ? CompanionBodyState.Cancelled : CompanionBodyState.Idle;
    }
}

public static class CompanionStateProjector
{
    public const int MaximumTrackedEventIds = 4096;

    public static DomainResult<AgentEventApplication> Apply(
        AgentRunProjection current,
        NormalizedAgentEvent agentEvent)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(agentEvent);
        if (agentEvent.RunId != current.RunId)
        {
            return Failure("agent_projection.run_mismatch", "The event belongs to a different run.");
        }

        if (current.SeenEvents.TryGetValue(agentEvent.Id, out NormalizedAgentEvent? priorEvent))
        {
            return priorEvent == agentEvent
                ? DomainResult.Success(
                    new AgentEventApplication(AgentEventDisposition.DuplicateIgnored, current))
                : Failure(
                    "agent_projection.event_id_conflict",
                    "An event ID was reused with conflicting normalized data.");
        }

        if (current.SeenEventCount >= MaximumTrackedEventIds)
        {
            return Failure("agent_projection.history_limit", "The bounded event identity history is full.");
        }

        if (current.LastSequence is not null && agentEvent.Sequence <= current.LastSequence)
        {
            return Failure("agent_projection.out_of_order", "Late or conflicting event sequence was rejected.");
        }

        if (current.LastOccurredUtc is not null && agentEvent.OccurredUtc < current.LastOccurredUtc)
        {
            return Failure("agent_projection.time_regressed", "Agent event time cannot move backwards.");
        }

        if (IsTerminal(current) && !IsAllowedAfterTerminal(agentEvent.Type))
        {
            return Failure("agent_projection.after_terminal", "A terminal run cannot return to active work.");
        }

        if (!current.HasStarted && RequiresStartedRun(agentEvent.Type))
        {
            return Failure("agent_projection.run_not_started", "Active work requires a committed run-start event.");
        }

        Dictionary<string, NormalizedAgentToolKind> tools =
            new(current.ActiveTools, StringComparer.Ordinal);
        HashSet<string> questions = new(current.PendingQuestionIds, StringComparer.Ordinal);
        HashSet<string> subagents = new(current.ActiveSubagentIds, StringComparer.Ordinal);
        Dictionary<AgentEventId, NormalizedAgentEvent> seenEvents =
            current.SeenEvents.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value);
        seenEvents.Add(agentEvent.Id, agentEvent);
        bool hasStarted = current.HasStarted;
        bool runActive = current.RunActive;
        bool observing = current.Observing;
        bool paused = current.Paused;
        bool blocked = current.Blocked;
        bool completed = current.Completed;
        bool cancelled = current.Cancelled;
        bool failed = current.Failed;
        bool permissionRevoked = current.PermissionRevoked;
        bool disconnected = current.Disconnected;

        DomainError? transitionError = ApplyEvent(
            agentEvent,
            tools,
            questions,
            subagents,
            ref hasStarted,
            ref runActive,
            ref observing,
            ref paused,
            ref blocked,
            ref completed,
            ref cancelled,
            ref failed,
            ref permissionRevoked,
            ref disconnected);
        if (transitionError is not null)
        {
            return Failure(transitionError.Code, transitionError.Message);
        }

        AgentRunProjection next = new(
            current.RunId,
            agentEvent.Sequence,
            agentEvent.OccurredUtc,
            seenEvents,
            tools,
            questions,
            subagents,
            hasStarted,
            runActive,
            observing,
            paused,
            blocked,
            completed,
            cancelled,
            failed,
            permissionRevoked,
            disconnected);
        return DomainResult.Success(new AgentEventApplication(AgentEventDisposition.Applied, next));
    }

    private static DomainError? ApplyEvent(
        NormalizedAgentEvent agentEvent,
        Dictionary<string, NormalizedAgentToolKind> tools,
        HashSet<string> questions,
        HashSet<string> subagents,
        ref bool hasStarted,
        ref bool runActive,
        ref bool observing,
        ref bool paused,
        ref bool blocked,
        ref bool completed,
        ref bool cancelled,
        ref bool failed,
        ref bool permissionRevoked,
        ref bool disconnected)
    {
        switch (agentEvent.Type)
        {
            case NormalizedAgentEventType.RunStarted:
                if (hasStarted)
                {
                    return Error("agent_projection.run_already_started", "A run can start only once.");
                }

                hasStarted = true;
                runActive = true;
                break;
            case NormalizedAgentEventType.RunCompleted:
                if (tools.Count > 0 || questions.Count > 0 || subagents.Count > 0)
                {
                    return Error(
                        "agent_projection.completion_with_active_work",
                        "A run cannot complete while bounded work or input remains active.");
                }

                runActive = false;
                completed = true;
                paused = false;
                blocked = false;
                break;
            case NormalizedAgentEventType.RunFailed:
                runActive = false;
                failed = true;
                break;
            case NormalizedAgentEventType.RunCancelled:
                runActive = false;
                cancelled = true;
                break;
            case NormalizedAgentEventType.RunPaused:
                paused = true;
                break;
            case NormalizedAgentEventType.RunResumed:
                paused = false;
                blocked = false;
                break;
            case NormalizedAgentEventType.RunBlocked:
                blocked = true;
                break;
            case NormalizedAgentEventType.ObservationStarted:
                observing = true;
                break;
            case NormalizedAgentEventType.ObservationStopped:
                observing = false;
                break;
            case NormalizedAgentEventType.ToolStarted:
                if (!tools.TryAdd(agentEvent.Data.ToolCallId!, agentEvent.Data.ToolKind!.Value))
                {
                    return Error("agent_projection.tool_already_active", "A tool call cannot start twice.");
                }

                break;
            case NormalizedAgentEventType.ToolCompleted:
                if (!tools.Remove(agentEvent.Data.ToolCallId!))
                {
                    return Error("agent_projection.tool_not_active", "A completed tool call was not active.");
                }

                break;
            case NormalizedAgentEventType.ToolFailed:
                if (!tools.Remove(agentEvent.Data.ToolCallId!))
                {
                    return Error("agent_projection.tool_not_active", "A failed tool call was not active.");
                }

                failed = true;
                break;
            case NormalizedAgentEventType.InputRequired:
                if (!questions.Add(agentEvent.Data.QuestionId!))
                {
                    return Error("agent_projection.question_already_pending", "An input question cannot open twice.");
                }

                break;
            case NormalizedAgentEventType.InputResolved:
                if (!questions.Remove(agentEvent.Data.QuestionId!))
                {
                    return Error("agent_projection.question_not_pending", "A resolved question was not pending.");
                }

                break;
            case NormalizedAgentEventType.SubagentStarted:
                if (!subagents.Add(agentEvent.Data.SubagentId!))
                {
                    return Error("agent_projection.subagent_already_active", "A subagent cannot start twice.");
                }

                break;
            case NormalizedAgentEventType.SubagentCompleted:
                if (!subagents.Remove(agentEvent.Data.SubagentId!))
                {
                    return Error("agent_projection.subagent_not_active", "A completed subagent was not active.");
                }

                break;
            case NormalizedAgentEventType.PermissionRevoked:
                permissionRevoked = true;
                break;
            case NormalizedAgentEventType.AdapterDisconnected:
                disconnected = true;
                break;
            case NormalizedAgentEventType.Heartbeat:
                break;
            default:
                return Error("agent_projection.event_unknown", "The normalized event type is unknown.");
        }

        return null;
    }

    private static bool IsTerminal(AgentRunProjection projection) =>
        projection.Failed ||
        projection.PermissionRevoked ||
        projection.Disconnected ||
        projection.Completed ||
        projection.Cancelled;

    private static bool IsAllowedAfterTerminal(NormalizedAgentEventType type) =>
        type is NormalizedAgentEventType.RunFailed or
            NormalizedAgentEventType.RunCancelled or
            NormalizedAgentEventType.PermissionRevoked or
            NormalizedAgentEventType.AdapterDisconnected or
            NormalizedAgentEventType.Heartbeat or
            NormalizedAgentEventType.ToolCompleted or
            NormalizedAgentEventType.ToolFailed or
            NormalizedAgentEventType.InputResolved or
            NormalizedAgentEventType.SubagentCompleted or
            NormalizedAgentEventType.ObservationStopped;

    private static bool RequiresStartedRun(NormalizedAgentEventType type) =>
        type is not NormalizedAgentEventType.RunStarted and
            not NormalizedAgentEventType.PermissionRevoked and
            not NormalizedAgentEventType.AdapterDisconnected and
            not NormalizedAgentEventType.Heartbeat;

    private static DomainResult<AgentEventApplication> Failure(string code, string message) =>
        DomainResult.Failure<AgentEventApplication>(code, message);

    private static DomainError Error(string code, string message) => new(code, message);
}
