using Tooltail.Domain.Agents;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Adapters.AgentEvents.Streaming;

public enum AgentEventStreamStatus
{
    Completed,
    Rejected,
    Cancelled,
    IoFailure,
}

public sealed record AgentEventStreamLimits
{
    public AgentEventStreamLimits(
        int maximumLineBytes,
        long maximumTotalBytes,
        int maximumEvents,
        int readBufferBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLineBytes, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumLineBytes, 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumTotalBytes, maximumLineBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumTotalBytes, 64L * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEvents, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            maximumEvents,
            CompanionStateProjector.MaximumTrackedEventIds);
        ArgumentOutOfRangeException.ThrowIfLessThan(readBufferBytes, 256);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(readBufferBytes, 64 * 1024);

        MaximumLineBytes = maximumLineBytes;
        MaximumTotalBytes = maximumTotalBytes;
        MaximumEvents = maximumEvents;
        ReadBufferBytes = readBufferBytes;
    }

    public int MaximumLineBytes { get; }

    public long MaximumTotalBytes { get; }

    public int MaximumEvents { get; }

    public int ReadBufferBytes { get; }

    public static AgentEventStreamLimits Default { get; } = new(
        maximumLineBytes: 64 * 1024,
        maximumTotalBytes: 16 * 1024 * 1024,
        maximumEvents: CompanionStateProjector.MaximumTrackedEventIds,
        readBufferBytes: 4 * 1024);
}

public sealed record AgentEventProjectionStep(
    int InputLine,
    long Sequence,
    NormalizedAgentEventType EventType,
    AgentEventDisposition Disposition,
    CompanionBodyProjection Body);

public sealed record AgentEventStreamResult(
    AgentEventStreamStatus Status,
    string ReasonCode,
    int InputLineCount,
    long InputByteCount,
    int AcceptedEventCount,
    int DuplicateEventCount,
    RunId? RunId,
    AgentRunProjection? FinalProjection,
    IReadOnlyList<AgentEventProjectionStep> Steps)
{
    public bool IsSuccess => Status == AgentEventStreamStatus.Completed;
}
