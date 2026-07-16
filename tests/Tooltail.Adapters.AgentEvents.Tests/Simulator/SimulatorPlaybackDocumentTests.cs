using Tooltail.Adapters.AgentEvents.Simulator;
using Tooltail.Domain.Agents;

namespace Tooltail.Adapters.AgentEvents.Tests.Simulator;

public sealed class SimulatorPlaybackDocumentTests
{
    [Fact]
    public async Task ValidTraceStartsIdleAndEndsAtCommittedReceipt()
    {
        Assert.True(
            SimulatorTraceCatalog.TryGet(
                "normal-start-tool-complete",
                out SimulatorTraceDefinition? trace));

        SimulatorPlaybackDocument document =
            await SimulatorPlaybackDocument.CreateAsync(trace);

        Assert.Equal(5, document.Frames.Count);
        Assert.Equal(CompanionBodyState.HomeIdle, document.Frames[0].Body.State);
        Assert.Equal(
            CompanionBodyState.CompletedReceipt,
            document.Frames[^1].Body.State);
        Assert.False(document.Frames[^1].AdapterSynthesized);
    }

    [Theory]
    [InlineData("malformed-jsonl")]
    [InlineData("delayed-time-regression")]
    [InlineData("out-of-order-sequence")]
    [InlineData("oversized-line")]
    [InlineData("event-stream-limit")]
    public async Task RejectedTraceEndsWithVisibleSyntheticDisconnect(string traceName)
    {
        Assert.True(SimulatorTraceCatalog.TryGet(traceName, out SimulatorTraceDefinition? trace));

        SimulatorPlaybackDocument document =
            await SimulatorPlaybackDocument.CreateAsync(trace);

        SimulatorPlaybackFrame final = document.Frames[^1];
        Assert.Equal(CompanionBodyState.Disconnected, final.Body.State);
        Assert.True(final.AdapterSynthesized);
        Assert.Equal(document.StreamReasonCode, final.EvidenceReasonCode);
    }

    [Fact]
    public async Task PlaybackFramesRetainExactNormalizedMetadataAndActiveFacts()
    {
        Assert.True(
            SimulatorTraceCatalog.TryGet(
                "parallel-two-units",
                out SimulatorTraceDefinition? trace));

        SimulatorPlaybackDocument document =
            await SimulatorPlaybackDocument.CreateAsync(trace);
        SimulatorPlaybackFrame parallel = document.Frames.Single(
            static frame => frame.Body.State == CompanionBodyState.ParallelWork);

        Assert.NotNull(parallel.NormalizedEvent);
        Assert.Equal(NormalizedAgentEventSource.Simulator, parallel.NormalizedEvent.Source);
        Assert.Equal(NormalizedAgentEventType.ToolStarted, parallel.NormalizedEvent.Type);
        Assert.NotEqual(Guid.Empty, parallel.NormalizedEvent.Id.Value);
        Assert.Equal(
            [
                NormalizedAgentToolKind.Code,
                NormalizedAgentToolKind.Terminal,
            ],
            parallel.ActiveToolKinds);
        Assert.Equal(0, parallel.PendingQuestionCount);
        Assert.Equal(0, parallel.ActiveSubagentCount);
    }
}
