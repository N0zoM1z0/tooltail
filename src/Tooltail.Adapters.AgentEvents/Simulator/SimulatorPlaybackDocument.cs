using Tooltail.Adapters.AgentEvents.Streaming;
using Tooltail.Domain.Agents;

namespace Tooltail.Adapters.AgentEvents.Simulator;

public sealed record SimulatorPlaybackFrame(
    int Index,
    int? InputLine,
    long? Sequence,
    NormalizedAgentEventType? EventType,
    AgentEventDisposition? Disposition,
    bool AdapterSynthesized,
    CompanionBodyProjection Body,
    NormalizedAgentEvent? NormalizedEvent,
    IReadOnlyList<NormalizedAgentToolKind> ActiveToolKinds,
    int PendingQuestionCount,
    int ActiveSubagentCount,
    string EvidenceReasonCode);

public sealed record SimulatorPlaybackDocument(
    string TraceName,
    string Description,
    AgentEventStreamStatus StreamStatus,
    string StreamReasonCode,
    IReadOnlyList<SimulatorPlaybackFrame> Frames)
{
    public static async Task<SimulatorPlaybackDocument> CreateAsync(
        SimulatorTraceDefinition trace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trace);
        SimulatorTraceVerification verification = await SimulatorTraceEvaluator.VerifyAsync(
            trace,
            cancellationToken).ConfigureAwait(false);
        AgentEventStreamResult actual = verification.Actual;
        List<SimulatorPlaybackFrame> frames =
        [
            new SimulatorPlaybackFrame(
                Index: 0,
                InputLine: null,
                Sequence: null,
                EventType: null,
                Disposition: null,
                AdapterSynthesized: true,
                new CompanionBodyProjection(
                    CompanionBodyState.HomeIdle,
                    ToolKind: null,
                    ParallelUnitCount: 0,
                    "body.home_idle"),
                NormalizedEvent: null,
                ActiveToolKinds: [],
                PendingQuestionCount: 0,
                ActiveSubagentCount: 0,
                "simulator.playback_reset"),
        ];
        foreach (AgentEventProjectionStep step in actual.Steps)
        {
            frames.Add(
                new SimulatorPlaybackFrame(
                    frames.Count,
                    step.InputLine,
                    step.Sequence,
                    step.EventType,
                    step.Disposition,
                    AdapterSynthesized: false,
                    step.Body,
                    step.Event,
                    step.ActiveToolKinds,
                    step.PendingQuestionCount,
                    step.ActiveSubagentCount,
                    step.Body.ReasonCode));
        }

        CompanionBodyProjection visibleFinalBody = actual.VisibleFinalBody;
        NormalizedAgentToolKind[] finalToolKinds = actual.FinalProjection is { } finalProjection
            ? finalProjection.ActiveTools
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => pair.Value)
                .ToArray()
            : [];
        if (!actual.IsSuccess &&
            (frames.Count == 1 || frames[^1].Body != visibleFinalBody))
        {
            frames.Add(
                new SimulatorPlaybackFrame(
                    frames.Count,
                    InputLine: null,
                    Sequence: null,
                    EventType: null,
                    Disposition: null,
                    AdapterSynthesized: true,
                    visibleFinalBody,
                    NormalizedEvent: null,
                    ActiveToolKinds: finalToolKinds,
                    PendingQuestionCount:
                        actual.FinalProjection?.PendingQuestionIds.Count ?? 0,
                    ActiveSubagentCount:
                        actual.FinalProjection?.ActiveSubagentIds.Count ?? 0,
                    actual.ReasonCode));
        }

        return new SimulatorPlaybackDocument(
            trace.Name,
            trace.Description,
            actual.Status,
            actual.ReasonCode,
            frames.AsReadOnly());
    }
}
