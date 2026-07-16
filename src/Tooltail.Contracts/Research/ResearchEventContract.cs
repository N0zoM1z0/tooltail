using Tooltail.Contracts.Json;

namespace Tooltail.Contracts.Research;

public enum ResearchEventType
{
    StudyOptedIn,
    StudyOptedOut,
    SessionStarted,
    SessionReset,
    BodyStatePresented,
    InspectorOpened,
    WindowLeaseIssued,
    WindowLeaseRevoked,
    FolderGrantIssued,
    FolderGrantRevoked,
    LessonCompleted,
    SkillCompiled,
    RehearsalCompleted,
    ExecutionCompleted,
    UndoCompleted,
    CorrectionCompleted,
    CapsuleExported,
    PauseRequested,
    CancelRequested,
    RatingSubmitted,
}

public enum ResearchBodyState
{
    HomeIdle,
    ScopedIdle,
    Observing,
    Working,
    ParallelWork,
    NeedsInput,
    Blocked,
    CompletedReceipt,
    Failed,
    PausedOrCancelled,
    PermissionRevoked,
    Disconnected,
}

public sealed record ResearchEventContract : IVersionedContract
{
    public required string SchemaVersion { get; init; }

    public required Guid EventId { get; init; }

    public required Guid StudyId { get; init; }

    public required Guid SessionId { get; init; }

    public required int Sequence { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required ResearchEventType Type { get; init; }

    public required bool Success { get; init; }

    public required string ReasonCode { get; init; }

    public long? DurationMilliseconds { get; init; }

    public int? Count { get; init; }

    public int? SkillVersion { get; init; }

    public ResearchBodyState? BodyState { get; init; }

    public string? PathToken { get; init; }

    public int? Rating { get; init; }
}
