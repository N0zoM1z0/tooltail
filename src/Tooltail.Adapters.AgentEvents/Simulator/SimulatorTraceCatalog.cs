using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Tooltail.Adapters.AgentEvents.Streaming;
using Tooltail.Contracts.AgentEvents;
using Tooltail.Contracts.Json;
using Tooltail.Domain.Agents;

namespace Tooltail.Adapters.AgentEvents.Simulator;

public static class SimulatorTraceCatalog
{
    private static readonly ReadOnlyCollection<SimulatorTraceDefinition> Catalog = CreateCatalog();

    private static readonly FrozenDictionary<string, SimulatorTraceDefinition> ByName =
        Catalog.ToFrozenDictionary(static trace => trace.Name, StringComparer.Ordinal);

    public static IReadOnlyList<SimulatorTraceDefinition> All => Catalog;

    public static bool TryGet(
        string name,
        [NotNullWhen(true)] out SimulatorTraceDefinition? trace)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            trace = null;
            return false;
        }

        return ByName.TryGetValue(name, out trace);
    }

    private static ReadOnlyCollection<SimulatorTraceDefinition> CreateCatalog()
    {
        List<SimulatorTraceDefinition> traces =
        [
            Trace(
                1,
                "normal-start-tool-complete",
                "A file tool starts, finishes, and returns a verified receipt.",
                [
                    EventSpec.Create(0, AgentEventType.RunStarted),
                    EventSpec.Create(
                        1,
                        AgentEventType.ToolStarted,
                        Tool(AgentToolKind.File, "file-1")),
                    EventSpec.Create(
                        2,
                        AgentEventType.ToolCompleted,
                        Tool(AgentToolKind.File, "file-1")),
                    EventSpec.Create(3, AgentEventType.RunCompleted),
                ],
                Success(
                    Working(),
                    Working(NormalizedAgentToolKind.File),
                    Working(),
                    Body(CompanionBodyState.CompletedReceipt, "body.completed_receipt"))),
            Trace(
                2,
                "observation-only",
                "Observation is visible without implying a mutating tool.",
                [
                    EventSpec.Create(0, AgentEventType.RunStarted),
                    EventSpec.Create(1, AgentEventType.ObservationStarted),
                    EventSpec.Create(2, AgentEventType.ObservationStopped),
                    EventSpec.Create(3, AgentEventType.RunCompleted),
                ],
                Success(
                    Working(),
                    Body(CompanionBodyState.Observing, "body.observing"),
                    Working(),
                    Body(CompanionBodyState.CompletedReceipt, "body.completed_receipt"))),
            Trace(
                3,
                "input-request-resolution",
                "A targeted question interrupts background work and then resolves.",
                [
                    EventSpec.Create(0, AgentEventType.RunStarted),
                    EventSpec.Create(
                        1,
                        AgentEventType.InputRequired,
                        new AgentEventDataContract { QuestionId = "question-1" }),
                    EventSpec.Create(
                        2,
                        AgentEventType.InputResolved,
                        new AgentEventDataContract { QuestionId = "question-1" }),
                    EventSpec.Create(3, AgentEventType.RunCompleted),
                ],
                Success(
                    Working(),
                    Body(CompanionBodyState.NeedsInput, "body.needs_input"),
                    Working(),
                    Body(CompanionBodyState.CompletedReceipt, "body.completed_receipt"))),
            Trace(
                4,
                "parallel-two-units",
                "Two active typed tools project an exact parallel unit count.",
                [
                    EventSpec.Create(0, AgentEventType.RunStarted),
                    EventSpec.Create(
                        1,
                        AgentEventType.ToolStarted,
                        Tool(AgentToolKind.Code, "code-1")),
                    EventSpec.Create(
                        2,
                        AgentEventType.ToolStarted,
                        Tool(AgentToolKind.Terminal, "terminal-1")),
                    EventSpec.Create(
                        3,
                        AgentEventType.ToolCompleted,
                        Tool(AgentToolKind.Code, "code-1")),
                    EventSpec.Create(
                        4,
                        AgentEventType.ToolCompleted,
                        Tool(AgentToolKind.Terminal, "terminal-1")),
                    EventSpec.Create(5, AgentEventType.RunCompleted),
                ],
                Success(
                    Working(),
                    Working(NormalizedAgentToolKind.Code),
                    Body(
                        CompanionBodyState.ParallelWork,
                        "body.parallel_work",
                        parallelUnitCount: 2),
                    Working(NormalizedAgentToolKind.Terminal),
                    Working(),
                    Body(CompanionBodyState.CompletedReceipt, "body.completed_receipt"))),
            Trace(
                5,
                "tool-and-run-failure",
                "A failed tool and terminal run remain visibly failed.",
                [
                    EventSpec.Create(0, AgentEventType.RunStarted),
                    EventSpec.Create(
                        1,
                        AgentEventType.ToolStarted,
                        Tool(AgentToolKind.Terminal, "terminal-1")),
                    EventSpec.Create(
                        2,
                        AgentEventType.ToolFailed,
                        Tool(AgentToolKind.Terminal, "terminal-1"),
                        severity: AgentEventSeverity.Error),
                    EventSpec.Create(
                        3,
                        AgentEventType.RunFailed,
                        severity: AgentEventSeverity.Error),
                ],
                Success(
                    Working(),
                    Working(NormalizedAgentToolKind.Terminal),
                    Body(CompanionBodyState.Failed, "body.failed"),
                    Body(CompanionBodyState.Failed, "body.failed"))),
            Trace(
                6,
                "pause-resume-cancel",
                "Pause, resume, and cancellation have stable non-working precedence.",
                [
                    EventSpec.Create(0, AgentEventType.RunStarted),
                    EventSpec.Create(1, AgentEventType.RunPaused),
                    EventSpec.Create(2, AgentEventType.RunResumed),
                    EventSpec.Create(3, AgentEventType.RunCancelled),
                ],
                Success(
                    Working(),
                    Body(CompanionBodyState.PausedOrCancelled, "body.paused"),
                    Working(),
                    Body(CompanionBodyState.PausedOrCancelled, "body.cancelled"))),
            Trace(
                7,
                "permission-revoked-mid-tool",
                "Revocation outranks a still-active tool until bounded cleanup arrives.",
                [
                    EventSpec.Create(0, AgentEventType.RunStarted),
                    EventSpec.Create(
                        1,
                        AgentEventType.ToolStarted,
                        Tool(AgentToolKind.File, "file-1")),
                    EventSpec.Create(
                        2,
                        AgentEventType.PermissionRevoked,
                        severity: AgentEventSeverity.Warning),
                    EventSpec.Create(
                        3,
                        AgentEventType.ToolCompleted,
                        Tool(AgentToolKind.File, "file-1")),
                ],
                Success(
                    Working(),
                    Working(NormalizedAgentToolKind.File),
                    Body(CompanionBodyState.PermissionRevoked, "body.permission_revoked"),
                    Body(CompanionBodyState.PermissionRevoked, "body.permission_revoked"))),
            Trace(
                8,
                "adapter-disconnect",
                "A disconnected adapter cannot continue to appear busy.",
                [
                    EventSpec.Create(0, AgentEventType.RunStarted),
                    EventSpec.Create(
                        1,
                        AgentEventType.ToolStarted,
                        Tool(AgentToolKind.Browser, "browser-1")),
                    EventSpec.Create(
                        2,
                        AgentEventType.AdapterDisconnected,
                        severity: AgentEventSeverity.Error),
                ],
                Success(
                    Working(),
                    Working(NormalizedAgentToolKind.Browser),
                    Body(CompanionBodyState.Disconnected, "body.disconnected"))),
            Trace(
                9,
                "blocked-and-resumed",
                "An ambiguous blocked run is explicit until a resume event clears it.",
                [
                    EventSpec.Create(0, AgentEventType.RunStarted),
                    EventSpec.Create(1, AgentEventType.RunBlocked),
                    EventSpec.Create(2, AgentEventType.RunResumed),
                    EventSpec.Create(3, AgentEventType.RunCompleted),
                ],
                Success(
                    Working(),
                    Body(CompanionBodyState.Blocked, "body.blocked"),
                    Working(),
                    Body(CompanionBodyState.CompletedReceipt, "body.completed_receipt"))),
            DuplicateTrace(),
            RawFailure(
                "malformed-jsonl",
                "Malformed JSON is rejected without producing a body state.",
                "{\"type\":\n"u8.ToArray(),
                AgentEventStreamLimits.Default,
                "contract.invalid_json"),
            DelayedTrace(),
            OutOfOrderTrace(),
            RawFailure(
                "oversized-line",
                "A line beyond its configured byte bound is rejected before parsing.",
                Enumerable.Repeat((byte)'x', 300).ToArray(),
                Limits(maximumLineBytes: 256, maximumTotalBytes: 512, maximumEvents: 4),
                "agent_stream.line_byte_limit"),
            BackpressureTrace(),
        ];
        return traces.AsReadOnly();
    }

    private static SimulatorTraceDefinition DuplicateTrace()
    {
        const int traceNumber = 10;
        byte[] started = Event(traceNumber, EventSpec.Create(0, AgentEventType.RunStarted));
        byte[] jsonl = JoinLines(
            started,
            started,
            Event(traceNumber, EventSpec.Create(1, AgentEventType.Heartbeat)),
            Event(traceNumber, EventSpec.Create(2, AgentEventType.RunCompleted)));
        return Definition(
            "duplicate-event",
            "An exact duplicate event ID is idempotent and remains visible in diagnostics.",
            jsonl,
            AgentEventStreamLimits.Default,
            new SimulatorTraceExpectation(
                AgentEventStreamStatus.Completed,
                "agent_stream.complete",
                AcceptedEventCount: 4,
                DuplicateEventCount: 1,
                [
                    Working(),
                    Working(),
                    Working(),
                    Body(CompanionBodyState.CompletedReceipt, "body.completed_receipt"),
                ]));
    }

    private static SimulatorTraceDefinition DelayedTrace()
    {
        const int traceNumber = 12;
        byte[] jsonl = JoinLines(
            Event(traceNumber, EventSpec.Create(0, AgentEventType.RunStarted)),
            Event(
                traceNumber,
                EventSpec.Create(
                    1,
                    AgentEventType.Heartbeat,
                    occurredOffsetSeconds: -1)));
        return Definition(
            "delayed-time-regression",
            "A delayed timestamp that regresses committed time is rejected.",
            jsonl,
            AgentEventStreamLimits.Default,
            Failure("agent_projection.time_regressed", 1, Working()));
    }

    private static SimulatorTraceDefinition OutOfOrderTrace()
    {
        const int traceNumber = 13;
        byte[] jsonl = JoinLines(
            Event(traceNumber, EventSpec.Create(0, AgentEventType.RunStarted)),
            Event(
                traceNumber,
                EventSpec.Create(
                    0,
                    AgentEventType.Heartbeat,
                    eventOrdinal: 2,
                    occurredOffsetSeconds: 1)));
        return Definition(
            "out-of-order-sequence",
            "A distinct event with a late sequence is rejected.",
            jsonl,
            AgentEventStreamLimits.Default,
            Failure("agent_projection.out_of_order", 1, Working()));
    }

    private static SimulatorTraceDefinition BackpressureTrace()
    {
        const int traceNumber = 15;
        byte[] jsonl = JoinLines(
            Event(traceNumber, EventSpec.Create(0, AgentEventType.RunStarted)),
            Event(traceNumber, EventSpec.Create(1, AgentEventType.Heartbeat)),
            Event(traceNumber, EventSpec.Create(2, AgentEventType.Heartbeat)));
        return Definition(
            "event-stream-limit",
            "The configured event bound stops the stream before unbounded projection.",
            jsonl,
            Limits(maximumLineBytes: 1024, maximumTotalBytes: 4096, maximumEvents: 2),
            Failure("agent_stream.event_limit", 2, Working(), Working()));
    }

    private static SimulatorTraceDefinition Trace(
        int traceNumber,
        string name,
        string description,
        IReadOnlyList<EventSpec> events,
        SimulatorTraceExpectation expectation) =>
        Definition(
            name,
            description,
            JoinLines(events.Select(item => Event(traceNumber, item)).ToArray()),
            AgentEventStreamLimits.Default,
            expectation);

    private static SimulatorTraceDefinition RawFailure(
        string name,
        string description,
        byte[] jsonl,
        AgentEventStreamLimits limits,
        string reasonCode) =>
        Definition(
            name,
            description,
            jsonl,
            limits,
            Failure(reasonCode, acceptedEventCount: 0));

    private static SimulatorTraceDefinition Definition(
        string name,
        string description,
        byte[] jsonl,
        AgentEventStreamLimits limits,
        SimulatorTraceExpectation expectation) =>
        new(name, description, jsonl, limits, expectation);

    private static SimulatorTraceExpectation Success(
        params CompanionBodyProjection[] bodies) =>
        new(
            AgentEventStreamStatus.Completed,
            "agent_stream.complete",
            bodies.Length,
            DuplicateEventCount: 0,
            bodies);

    private static SimulatorTraceExpectation Failure(
        string reasonCode,
        int acceptedEventCount,
        params CompanionBodyProjection[] bodies) =>
        new(
            AgentEventStreamStatus.Rejected,
            reasonCode,
            acceptedEventCount,
            DuplicateEventCount: 0,
            bodies);

    private static CompanionBodyProjection Working(
        NormalizedAgentToolKind? toolKind = null) =>
        Body(
            CompanionBodyState.Working,
            toolKind is null ? "body.working" : "body.working_tool",
            toolKind);

    private static CompanionBodyProjection Body(
        CompanionBodyState state,
        string reasonCode,
        NormalizedAgentToolKind? toolKind = null,
        int parallelUnitCount = 0) =>
        new(state, toolKind, parallelUnitCount, reasonCode);

    private static AgentEventDataContract Tool(AgentToolKind kind, string id) =>
        new()
        {
            ToolKind = kind,
            ToolCallId = id,
        };

    private static AgentEventStreamLimits Limits(
        int maximumLineBytes,
        long maximumTotalBytes,
        int maximumEvents) =>
        new(
            maximumLineBytes,
            maximumTotalBytes,
            maximumEvents,
            readBufferBytes: 256);

    private static byte[] Event(int traceNumber, EventSpec item) =>
        ContractJson.Serialize(
            new AgentEventContract
            {
                SchemaVersion = ContractVersions.V1,
                EventId = Guid.Parse(
                    $"20000000-{traceNumber:D4}-4000-8000-{item.EventOrdinal:D12}"),
                RunId = Guid.Parse(
                    $"10000000-{traceNumber:D4}-4000-8000-{traceNumber:D12}"),
                Sequence = item.Sequence,
                OccurredAt = new DateTimeOffset(2026, 7, 16, 4, 0, 0, TimeSpan.Zero)
                    .AddMinutes(traceNumber)
                    .AddSeconds(item.OccurredOffsetSeconds),
                Source = AgentEventSource.Simulator,
                Type = item.Type,
                Severity = item.Severity,
                Data = item.Data,
            });

    private static byte[] JoinLines(params byte[][] lines)
    {
        int length = checked(lines.Sum(static line => line.Length + 1));
        byte[] output = new byte[length];
        int offset = 0;
        foreach (byte[] line in lines)
        {
            line.CopyTo(output, offset);
            offset += line.Length;
            output[offset++] = (byte)'\n';
        }

        return output;
    }

    private sealed record EventSpec(
        long Sequence,
        int EventOrdinal,
        int OccurredOffsetSeconds,
        AgentEventType Type,
        AgentEventSeverity Severity,
        AgentEventDataContract Data)
    {
        public static EventSpec Create(
            long sequence,
            AgentEventType type,
            AgentEventDataContract? data = null,
            AgentEventSeverity severity = AgentEventSeverity.Info,
            int? eventOrdinal = null,
            int? occurredOffsetSeconds = null) =>
            new(
                sequence,
                eventOrdinal ?? checked((int)sequence + 1),
                occurredOffsetSeconds ?? checked((int)sequence),
                type,
                severity,
                data ?? new AgentEventDataContract());
    }
}
