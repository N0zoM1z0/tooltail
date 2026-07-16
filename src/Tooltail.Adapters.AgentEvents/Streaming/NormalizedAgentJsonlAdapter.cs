using Tooltail.Adapters.AgentEvents.Normalization;
using Tooltail.Contracts.AgentEvents;
using Tooltail.Contracts.Json;
using Tooltail.Domain.Agents;
using Tooltail.Domain.Common;

namespace Tooltail.Adapters.AgentEvents.Streaming;

public static class NormalizedAgentJsonlAdapter
{
    public static async Task<AgentEventStreamResult> ReadAsync(
        Stream input,
        NormalizedAgentEventSource expectedSource,
        AgentEventStreamLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(expectedSource))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedSource));
        }

        limits ??= AgentEventStreamLimits.Default;
        List<AgentEventProjectionStep> steps = [];
        AgentRunProjection? projection = null;
        int acceptedEventCount = 0;
        int duplicateEventCount = 0;
        string? handlerFailure = null;
        BoundedJsonlReadResult read = await BoundedJsonlLineReader.ReadAsync(
            input,
            limits,
            (line, lineNumber) =>
            {
                if (acceptedEventCount >= limits.MaximumEvents)
                {
                    return handlerFailure = "agent_stream.event_limit";
                }

                ContractParseResult<AgentEventContract> parsed =
                    ContractJson.ParseAgentEvent(line.Span);
                if (!parsed.IsSuccess)
                {
                    return handlerFailure = parsed.Error!.Code;
                }

                DomainResult<NormalizedAgentEvent> mapped =
                    NormalizedAgentEventMapper.Map(parsed.Value!);
                if (!mapped.IsSuccess)
                {
                    return handlerFailure = mapped.Error!.Code;
                }

                NormalizedAgentEvent agentEvent = mapped.Value!;
                if (agentEvent.Source != expectedSource)
                {
                    return handlerFailure = "agent_stream.source_mismatch";
                }

                projection ??= AgentRunProjection.Empty(agentEvent.RunId);
                if (projection.RunId != agentEvent.RunId)
                {
                    return handlerFailure = "agent_stream.run_mismatch";
                }

                DomainResult<AgentEventApplication> applied =
                    CompanionStateProjector.Apply(projection, agentEvent);
                if (!applied.IsSuccess)
                {
                    return handlerFailure = applied.Error!.Code;
                }

                acceptedEventCount++;
                if (applied.Value!.Disposition == AgentEventDisposition.DuplicateIgnored)
                {
                    duplicateEventCount++;
                }

                projection = applied.Value.Projection;
                steps.Add(
                    new AgentEventProjectionStep(
                        lineNumber,
                        agentEvent.Sequence,
                        agentEvent.Type,
                        applied.Value.Disposition,
                        projection.Body,
                        agentEvent,
                        projection.ActiveTools
                            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                            .Select(static pair => pair.Value)
                            .ToArray(),
                        projection.PendingQuestionIds.Count,
                        projection.ActiveSubagentIds.Count));
                return null;
            },
            cancellationToken).ConfigureAwait(false);
        string reasonCode = handlerFailure ?? read.ReasonCode;
        AgentEventStreamStatus status = read.Status;
        if (read.Status == AgentEventStreamStatus.Completed && projection is null)
        {
            status = AgentEventStreamStatus.Rejected;
            reasonCode = "agent_stream.empty";
        }

        return new AgentEventStreamResult(
            status,
            reasonCode,
            read.LineCount,
            read.ByteCount,
            acceptedEventCount,
            duplicateEventCount,
            projection?.RunId,
            projection,
            steps);
    }
}
