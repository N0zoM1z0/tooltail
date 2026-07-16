using Tooltail.Contracts.Research;

namespace Tooltail.Infrastructure.LocalResearch;

public sealed record LocalResearchOptions(string ApplicationRootPath)
{
    public const int MaximumEventCount = 1_000;
    public const long MaximumEventBytes = 8 * 1024 * 1024;
    public const int MaximumPreviewCharacters = 256 * 1024;
}

public sealed record ResearchStoreStatus(
    bool IsSuccess,
    string ReasonCode,
    bool IsEnabled,
    Guid? StudyId,
    Guid? SessionId,
    int EventCount,
    long EventBytes);

public sealed record ResearchEventInput(
    ResearchEventType Type,
    bool Success,
    string ReasonCode,
    long? DurationMilliseconds = null,
    int? Count = null,
    int? SkillVersion = null,
    ResearchBodyState? BodyState = null,
    string? PathToken = null,
    int? Rating = null);

public sealed record ResearchWriteResult(
    bool IsSuccess,
    string ReasonCode,
    ResearchEventContract? Event);

public sealed record ResearchPreviewResult(
    bool IsSuccess,
    string ReasonCode,
    string PreviewJsonl,
    int EventCount,
    int ByteCount,
    bool IsTruncated);

public sealed record ResearchExportResult(
    bool IsSuccess,
    string ReasonCode,
    string? CanonicalPath,
    int EventCount,
    int ByteCount);

public sealed record ResearchTokenResult(
    bool IsSuccess,
    string ReasonCode,
    string? Token);
