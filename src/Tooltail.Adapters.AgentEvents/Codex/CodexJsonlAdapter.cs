using Tooltail.Adapters.AgentEvents.Streaming;
using Tooltail.Domain.Agents;
using Tooltail.Domain.Common;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Adapters.AgentEvents.Codex;

public static class CodexJsonlAdapter
{
    public static async Task<CodexEventStreamResult> ReadAsync(
        Stream input,
        RunId runId,
        AgentEventStreamLimits? limits = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        limits ??= AgentEventStreamLimits.Default;
        var normalizer = new CodexRawEventNormalizer(runId, timeProvider);
        AgentRunProjection projection = AgentRunProjection.Empty(runId);
        List<CodexProjectionStep> steps = [];
        int processedRawEventCount = 0;
        int emittedCount = 0;
        int ignoredKnownCount = 0;
        int ignoredUnknownCount = 0;
        string? handlerFailure = null;
        BoundedJsonlReadResult read = await BoundedJsonlLineReader.ReadAsync(
            input,
            limits,
            (line, lineNumber) =>
            {
                if (processedRawEventCount >= limits.MaximumEvents)
                {
                    return handlerFailure = "codex_adapter.event_limit";
                }

                processedRawEventCount++;
                CodexRawEventMapResult mapped = normalizer.Map(line);
                switch (mapped.Disposition)
                {
                    case CodexRawEventDisposition.IgnoredKnown:
                        ignoredKnownCount++;
                        return null;
                    case CodexRawEventDisposition.IgnoredUnknown:
                        ignoredUnknownCount++;
                        return null;
                    case CodexRawEventDisposition.Rejected:
                        return handlerFailure = mapped.ReasonCode;
                    case CodexRawEventDisposition.Emitted:
                        string? applyFailure = Apply(
                            mapped.Event!,
                            lineNumber,
                            adapterSynthesized: false,
                            ref emittedCount,
                            ref projection,
                            steps,
                            out handlerFailure);
                        if (applyFailure is not null)
                        {
                            return applyFailure;
                        }

                        return mapped.Event!.Type ==
                            NormalizedAgentEventType.AdapterDisconnected
                            ? handlerFailure = mapped.ReasonCode
                            : null;
                    default:
                        return handlerFailure = "codex_adapter.disposition_unknown";
                }
            },
            cancellationToken).ConfigureAwait(false);

        AgentEventStreamStatus status = read.Status;
        string reasonCode = handlerFailure ?? read.ReasonCode;
        NormalizedAgentEvent? closingEvent = null;
        if (read.Status == AgentEventStreamStatus.Cancelled)
        {
            closingEvent = normalizer.CreateCancellationEvent();
        }
        else if (!normalizer.IsTerminal)
        {
            string disconnectStatus = read.Status switch
            {
                AgentEventStreamStatus.Completed => "unexpected_eof",
                AgentEventStreamStatus.IoFailure => "stream_io_failure",
                _ => "stream_rejected",
            };
            closingEvent = normalizer.CreateDisconnectEvent(disconnectStatus);
            if (read.Status == AgentEventStreamStatus.Completed)
            {
                status = AgentEventStreamStatus.Rejected;
                reasonCode = "codex_adapter.unexpected_eof";
            }
        }

        if (closingEvent is not null)
        {
            string? applyFailure = Apply(
                closingEvent,
                inputLine: null,
                adapterSynthesized: true,
                ref emittedCount,
                ref projection,
                steps,
                out _);
            if (applyFailure is not null)
            {
                status = AgentEventStreamStatus.Rejected;
                reasonCode = applyFailure;
            }
        }

        return new CodexEventStreamResult(
            status,
            reasonCode,
            read.LineCount,
            read.ByteCount,
            processedRawEventCount,
            emittedCount,
            ignoredKnownCount,
            ignoredUnknownCount,
            runId,
            projection,
            steps.AsReadOnly());
    }

    private static string? Apply(
        NormalizedAgentEvent agentEvent,
        int? inputLine,
        bool adapterSynthesized,
        ref int emittedCount,
        ref AgentRunProjection projection,
        List<CodexProjectionStep> steps,
        out string? failure)
    {
        DomainResult<AgentEventApplication> applied = CompanionStateProjector.Apply(
            projection,
            agentEvent);
        if (!applied.IsSuccess)
        {
            return failure = applied.Error!.Code;
        }

        emittedCount++;
        projection = applied.Value!.Projection;
        steps.Add(
            new CodexProjectionStep(
                inputLine,
                adapterSynthesized,
                agentEvent.Sequence,
                agentEvent.Type,
                applied.Value.Disposition,
                projection.Body));
        failure = null;
        return null;
    }
}
