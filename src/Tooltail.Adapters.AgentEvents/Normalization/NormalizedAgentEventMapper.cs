using Tooltail.Contracts.AgentEvents;
using Tooltail.Contracts.Json;
using Tooltail.Domain.Agents;
using Tooltail.Domain.Common;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Adapters.AgentEvents.Normalization;

public static class NormalizedAgentEventMapper
{
    public static DomainResult<NormalizedAgentEvent> Map(AgentEventContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (contract.SchemaVersion != ContractVersions.V1)
        {
            return Failure("agent_event.schema_unsupported", "The normalized event schema is not supported.");
        }

        if (contract.Data is null)
        {
            return Failure("agent_event.data_missing", "Normalized event data is required.");
        }

        NormalizedAgentEventSource? source = MapSource(contract.Source);
        NormalizedAgentEventType? type = MapType(contract.Type);
        NormalizedAgentEventSeverity? severity = MapSeverity(contract.Severity);
        NormalizedAgentToolKind? toolKind = MapToolKind(contract.Data.ToolKind);
        if (source is null || type is null || severity is null ||
            (contract.Data.ToolKind is not null && toolKind is null))
        {
            return Failure("agent_event.enum_unknown", "A normalized event enum is unknown.");
        }

        DomainResult<NormalizedAgentEventData> data = NormalizedAgentEventData.Create(
            toolKind,
            contract.Data.ToolCallId,
            contract.Data.QuestionId,
            contract.Data.SubagentId,
            contract.Data.DisplayLabel,
            contract.Data.StatusCode,
            contract.Data.Progress,
            contract.Data.ParallelUnitCount);
        if (!data.IsSuccess)
        {
            return Failure(data.Error!.Code, data.Error.Message);
        }

        return NormalizedAgentEvent.Create(
            contract.EventId == Guid.Empty ? default : new AgentEventId(contract.EventId),
            contract.RunId == Guid.Empty ? default : new RunId(contract.RunId),
            contract.Sequence,
            contract.OccurredAt.ToUniversalTime(),
            source.Value,
            type.Value,
            severity.Value,
            data.Value!);
    }

    private static NormalizedAgentEventSource? MapSource(AgentEventSource source) =>
        source switch
        {
            AgentEventSource.Simulator => NormalizedAgentEventSource.Simulator,
            AgentEventSource.GenericJsonl => NormalizedAgentEventSource.GenericJsonl,
            AgentEventSource.CodexExecJsonl => NormalizedAgentEventSource.CodexExecJsonl,
            _ => null,
        };

    private static NormalizedAgentEventType? MapType(AgentEventType type) =>
        type switch
        {
            AgentEventType.RunStarted => NormalizedAgentEventType.RunStarted,
            AgentEventType.RunCompleted => NormalizedAgentEventType.RunCompleted,
            AgentEventType.RunFailed => NormalizedAgentEventType.RunFailed,
            AgentEventType.RunCancelled => NormalizedAgentEventType.RunCancelled,
            AgentEventType.RunPaused => NormalizedAgentEventType.RunPaused,
            AgentEventType.RunResumed => NormalizedAgentEventType.RunResumed,
            AgentEventType.RunBlocked => NormalizedAgentEventType.RunBlocked,
            AgentEventType.ObservationStarted => NormalizedAgentEventType.ObservationStarted,
            AgentEventType.ObservationStopped => NormalizedAgentEventType.ObservationStopped,
            AgentEventType.ToolStarted => NormalizedAgentEventType.ToolStarted,
            AgentEventType.ToolCompleted => NormalizedAgentEventType.ToolCompleted,
            AgentEventType.ToolFailed => NormalizedAgentEventType.ToolFailed,
            AgentEventType.InputRequired => NormalizedAgentEventType.InputRequired,
            AgentEventType.InputResolved => NormalizedAgentEventType.InputResolved,
            AgentEventType.SubagentStarted => NormalizedAgentEventType.SubagentStarted,
            AgentEventType.SubagentCompleted => NormalizedAgentEventType.SubagentCompleted,
            AgentEventType.PermissionRevoked => NormalizedAgentEventType.PermissionRevoked,
            AgentEventType.Heartbeat => NormalizedAgentEventType.Heartbeat,
            AgentEventType.AdapterDisconnected => NormalizedAgentEventType.AdapterDisconnected,
            _ => null,
        };

    private static NormalizedAgentEventSeverity? MapSeverity(AgentEventSeverity severity) =>
        severity switch
        {
            AgentEventSeverity.Trace => NormalizedAgentEventSeverity.Trace,
            AgentEventSeverity.Info => NormalizedAgentEventSeverity.Info,
            AgentEventSeverity.Warning => NormalizedAgentEventSeverity.Warning,
            AgentEventSeverity.Error => NormalizedAgentEventSeverity.Error,
            _ => null,
        };

    private static NormalizedAgentToolKind? MapToolKind(AgentToolKind? toolKind) =>
        toolKind switch
        {
            null => null,
            AgentToolKind.Browser => NormalizedAgentToolKind.Browser,
            AgentToolKind.Code => NormalizedAgentToolKind.Code,
            AgentToolKind.Email => NormalizedAgentToolKind.Email,
            AgentToolKind.File => NormalizedAgentToolKind.File,
            AgentToolKind.Terminal => NormalizedAgentToolKind.Terminal,
            AgentToolKind.Other => NormalizedAgentToolKind.Other,
            _ => null,
        };

    private static DomainResult<NormalizedAgentEvent> Failure(string code, string message) =>
        DomainResult.Failure<NormalizedAgentEvent>(code, message);
}
