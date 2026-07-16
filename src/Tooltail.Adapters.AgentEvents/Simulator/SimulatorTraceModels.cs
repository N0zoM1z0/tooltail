using Tooltail.Adapters.AgentEvents.Streaming;
using Tooltail.Domain.Agents;

namespace Tooltail.Adapters.AgentEvents.Simulator;

public sealed record SimulatorTraceExpectation(
    AgentEventStreamStatus Status,
    string ReasonCode,
    int AcceptedEventCount,
    int DuplicateEventCount,
    IReadOnlyList<CompanionBodyProjection> Bodies);

public sealed class SimulatorTraceDefinition
{
    private readonly byte[] jsonl;

    internal SimulatorTraceDefinition(
        string name,
        string description,
        byte[] jsonl,
        AgentEventStreamLimits limits,
        SimulatorTraceExpectation expectation)
    {
        Name = name;
        Description = description;
        this.jsonl = jsonl.ToArray();
        Limits = limits;
        Expectation = expectation;
    }

    public string Name { get; }

    public string Description { get; }

    public int JsonlByteCount => jsonl.Length;

    public AgentEventStreamLimits Limits { get; }

    public SimulatorTraceExpectation Expectation { get; }

    public Stream OpenRead() => new MemoryStream(jsonl, writable: false);

    public byte[] ExportJsonl() => jsonl.ToArray();
}

public sealed record SimulatorTraceVerification(
    SimulatorTraceDefinition Trace,
    AgentEventStreamResult Actual,
    IReadOnlyList<string> MismatchCodes)
{
    public bool IsMatch => MismatchCodes.Count == 0;
}

public sealed record SimulatorTraceStepReport(
    int InputLine,
    long Sequence,
    NormalizedAgentEventType EventType,
    AgentEventDisposition Disposition,
    CompanionBodyState BodyState,
    NormalizedAgentToolKind? ToolKind,
    int ParallelUnitCount,
    string BodyReasonCode);

public sealed record SimulatorTraceReport(
    string Trace,
    string Description,
    int JsonlByteCount,
    bool MatchesExpected,
    AgentEventStreamStatus Status,
    string ReasonCode,
    int InputLineCount,
    long InputByteCount,
    int AcceptedEventCount,
    int DuplicateEventCount,
    Guid? RunId,
    IReadOnlyList<string> MismatchCodes,
    IReadOnlyList<SimulatorTraceStepReport> Steps);

public sealed record SimulatorTraceCatalogReport(
    string SchemaVersion,
    bool AllMatched,
    IReadOnlyList<SimulatorTraceReport> Traces);
