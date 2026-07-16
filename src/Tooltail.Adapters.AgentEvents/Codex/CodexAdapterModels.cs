using Tooltail.Adapters.AgentEvents.Streaming;
using Tooltail.Domain.Agents;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Adapters.AgentEvents.Codex;

public enum CodexRawEventDisposition
{
    Emitted,
    IgnoredKnown,
    IgnoredUnknown,
    Rejected,
}

public sealed record CodexRawEventMapResult(
    CodexRawEventDisposition Disposition,
    string ReasonCode,
    NormalizedAgentEvent? Event);

public sealed record CodexProjectionStep(
    int? InputLine,
    bool AdapterSynthesized,
    long Sequence,
    NormalizedAgentEventType EventType,
    AgentEventDisposition Disposition,
    CompanionBodyProjection Body);

public sealed record CodexEventStreamResult(
    AgentEventStreamStatus Status,
    string ReasonCode,
    int InputLineCount,
    long InputByteCount,
    int ProcessedRawEventCount,
    int EmittedEventCount,
    int IgnoredKnownEventCount,
    int IgnoredUnknownEventCount,
    RunId RunId,
    AgentRunProjection FinalProjection,
    IReadOnlyList<CodexProjectionStep> Steps)
{
    public bool IsSuccess => Status == AgentEventStreamStatus.Completed;
}
