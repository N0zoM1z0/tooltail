using Tooltail.Domain.Agents;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Domain.Tests;

public sealed class AgentProjectionTests
{
    private static readonly RunId RunId =
        new(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));

    private static readonly DateTimeOffset Now =
        new(2026, 7, 16, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void InterruptiveBodyStatePrecedenceIsDeterministic()
    {
        AgentRunProjection projection = AgentRunProjection.Empty(RunId);
        Assert.Equal(CompanionBodyState.HomeIdle, projection.BodyState);

        projection = Apply(projection, Event(0, NormalizedAgentEventType.RunStarted));
        Assert.Equal(CompanionBodyState.Working, projection.BodyState);

        projection = Apply(
            projection,
            Event(
                1,
                NormalizedAgentEventType.InputRequired,
                Data(questionId: "question-1")));
        Assert.Equal(CompanionBodyState.NeedsInput, projection.BodyState);

        projection = Apply(projection, Event(2, NormalizedAgentEventType.RunBlocked));
        Assert.Equal(CompanionBodyState.NeedsInput, projection.BodyState);

        projection = Apply(projection, Event(3, NormalizedAgentEventType.PermissionRevoked));
        Assert.Equal(CompanionBodyState.PermissionRevoked, projection.BodyState);

        projection = Apply(projection, Event(4, NormalizedAgentEventType.RunFailed));
        Assert.Equal(CompanionBodyState.Failed, projection.BodyState);
    }

    [Fact]
    public void DuplicateEventIdIsIdempotentButConflictingLateSequenceFails()
    {
        NormalizedAgentEvent started = Event(0, NormalizedAgentEventType.RunStarted);
        AgentRunProjection applied = Apply(AgentRunProjection.Empty(RunId), started);

        var duplicate = CompanionStateProjector.Apply(applied, started);
        var conflictingIdentity = CompanionStateProjector.Apply(
            applied,
            Event(0, NormalizedAgentEventType.Heartbeat));
        var late = CompanionStateProjector.Apply(
            applied,
            Event(0, NormalizedAgentEventType.Heartbeat, eventIdNumber: 99));

        Assert.True(duplicate.IsSuccess);
        Assert.Equal(AgentEventDisposition.DuplicateIgnored, duplicate.Value!.Disposition);
        Assert.Same(applied, duplicate.Value.Projection);
        Assert.Equal("agent_projection.event_id_conflict", conflictingIdentity.Error?.Code);
        Assert.False(late.IsSuccess);
        Assert.Equal("agent_projection.out_of_order", late.Error?.Code);
    }

    [Fact]
    public void ActiveToolsMustCloseBeforeVerifiedCompletion()
    {
        AgentRunProjection projection = Apply(
            AgentRunProjection.Empty(RunId),
            Event(0, NormalizedAgentEventType.RunStarted));
        projection = Apply(
            projection,
            Event(
                1,
                NormalizedAgentEventType.ToolStarted,
                Data(toolKind: NormalizedAgentToolKind.File, toolCallId: "file-1")));

        var prematureCompletion = CompanionStateProjector.Apply(
            projection,
            Event(2, NormalizedAgentEventType.RunCompleted));
        projection = Apply(
            projection,
            Event(
                2,
                NormalizedAgentEventType.ToolCompleted,
                Data(toolKind: NormalizedAgentToolKind.File, toolCallId: "file-1")));
        projection = Apply(projection, Event(3, NormalizedAgentEventType.RunCompleted));

        Assert.False(prematureCompletion.IsSuccess);
        Assert.Equal(
            "agent_projection.completion_with_active_work",
            prematureCompletion.Error?.Code);
        Assert.Equal(CompanionBodyState.CompletedReceipt, projection.BodyState);
        Assert.Empty(projection.ActiveTools);
    }

    [Fact]
    public void ObservationCleanupAfterCompletionCannotReactivateRun()
    {
        AgentRunProjection projection = Apply(
            AgentRunProjection.Empty(RunId),
            Event(0, NormalizedAgentEventType.RunStarted));
        projection = Apply(projection, Event(1, NormalizedAgentEventType.ObservationStarted));
        projection = Apply(projection, Event(2, NormalizedAgentEventType.RunCompleted));
        Assert.Equal(CompanionBodyState.Observing, projection.BodyState);

        projection = Apply(projection, Event(3, NormalizedAgentEventType.ObservationStopped));
        var restarted = CompanionStateProjector.Apply(
            projection,
            Event(4, NormalizedAgentEventType.RunResumed));

        Assert.Equal(CompanionBodyState.CompletedReceipt, projection.BodyState);
        Assert.False(restarted.IsSuccess);
        Assert.Equal("agent_projection.after_terminal", restarted.Error?.Code);
    }

    [Fact]
    public void NormalizedDataRejectsControlTextAndInvalidAuthorityLikeFields()
    {
        var controlText = NormalizedAgentEventData.Create(displayLabel: "unsafe\u001b[31m");
        var invalidId = NormalizedAgentEventData.Create(toolCallId: "contains space");
        var invalidStatus = NormalizedAgentEventData.Create(statusCode: "NOT_SAFE");
        var invalidProgress = NormalizedAgentEventData.Create(progress: 1.1m);

        Assert.Equal("agent_event.display_label_invalid", controlText.Error?.Code);
        Assert.Equal("agent_event.opaque_id_invalid", invalidId.Error?.Code);
        Assert.Equal("agent_event.status_code_invalid", invalidStatus.Error?.Code);
        Assert.Equal("agent_event.progress_out_of_range", invalidProgress.Error?.Code);
    }

    [Fact]
    public void ToolEventRequiresTypedToolIdentity()
    {
        var result = NormalizedAgentEvent.Create(
            new AgentEventId(Guid.NewGuid()),
            RunId,
            0,
            Now,
            NormalizedAgentEventSource.Simulator,
            NormalizedAgentEventType.ToolStarted,
            NormalizedAgentEventSeverity.Info,
            Data());

        Assert.False(result.IsSuccess);
        Assert.Equal("agent_event.required_data_missing", result.Error?.Code);
    }

    [Fact]
    public void BodyProjectionCarriesScopeToolAndParallelParameters()
    {
        AgentRunProjection projection = AgentRunProjection.Empty(RunId);
        CompanionBodyProjection scoped = CompanionBodyProjector.Project(
            projection,
            hasVisibleScope: true);
        Assert.Equal(CompanionBodyState.ScopedIdle, scoped.State);
        Assert.Equal("body.scoped_idle", scoped.ReasonCode);

        projection = Apply(projection, Event(0, NormalizedAgentEventType.RunStarted));
        projection = Apply(
            projection,
            Event(
                1,
                NormalizedAgentEventType.ToolStarted,
                Data(toolKind: NormalizedAgentToolKind.Code, toolCallId: "code-1")));
        Assert.Equal(CompanionBodyState.Working, projection.BodyState);
        Assert.Equal(NormalizedAgentToolKind.Code, projection.Body.ToolKind);
        Assert.Equal("body.working_tool", projection.Body.ReasonCode);

        projection = Apply(
            projection,
            Event(
                2,
                NormalizedAgentEventType.Heartbeat,
                Data(parallelUnitCount: 3)));
        Assert.Equal(CompanionBodyState.ParallelWork, projection.BodyState);
        Assert.Equal(3, projection.Body.ParallelUnitCount);
        Assert.Null(projection.Body.ToolKind);
    }

    [Fact]
    public void CancellationCannotBeHiddenByAnActiveTool()
    {
        AgentRunProjection projection = Apply(
            AgentRunProjection.Empty(RunId),
            Event(0, NormalizedAgentEventType.RunStarted));
        projection = Apply(
            projection,
            Event(
                1,
                NormalizedAgentEventType.ToolStarted,
                Data(toolKind: NormalizedAgentToolKind.Terminal, toolCallId: "tool-1")));

        projection = Apply(projection, Event(2, NormalizedAgentEventType.RunCancelled));

        Assert.Equal(CompanionBodyState.PausedOrCancelled, projection.BodyState);
        Assert.Equal("body.cancelled", projection.Body.ReasonCode);
        Assert.Single(projection.ActiveTools);
    }

    private static NormalizedAgentEvent Event(
        long sequence,
        NormalizedAgentEventType type,
        NormalizedAgentEventData? data = null,
        int? eventIdNumber = null) =>
        NormalizedAgentEvent.Create(
            new AgentEventId(
                Guid.Parse($"00000000-0000-4000-8000-{(eventIdNumber ?? sequence + 1):D12}")),
            RunId,
            sequence,
            Now.AddSeconds(sequence),
            NormalizedAgentEventSource.Simulator,
            type,
            NormalizedAgentEventSeverity.Info,
            data ?? Data()).Value!;

    private static NormalizedAgentEventData Data(
        NormalizedAgentToolKind? toolKind = null,
        string? toolCallId = null,
        string? questionId = null,
        int? parallelUnitCount = null) =>
        NormalizedAgentEventData.Create(
            toolKind: toolKind,
            toolCallId: toolCallId,
            questionId: questionId,
            parallelUnitCount: parallelUnitCount).Value!;

    private static AgentRunProjection Apply(
        AgentRunProjection projection,
        NormalizedAgentEvent agentEvent)
    {
        var result = CompanionStateProjector.Apply(projection, agentEvent);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        return result.Value!.Projection;
    }
}
