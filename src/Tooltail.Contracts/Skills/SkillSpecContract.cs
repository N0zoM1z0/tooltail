using System.Text.Json.Serialization;
using Tooltail.Contracts.Json;

namespace Tooltail.Contracts.Skills;

public sealed record SkillSpecContract : IVersionedContract
{
    public required string SchemaVersion { get; init; }

    public required Guid SkillId { get; init; }

    public required int Version { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required SkillCompilerContract Compiler { get; init; }

    public required SkillApplicabilityContract Applicability { get; init; }

    public required IReadOnlyList<SkillVariableContract> Variables { get; init; }

    public required IReadOnlyList<SkillStepContract> Steps { get; init; }

    public required SkillPolicyContract Policy { get; init; }

    public required SkillVerificationContract Verification { get; init; }

    public required SkillProvenanceContract Provenance { get; init; }

    public required SkillCompatibilityContract Compatibility { get; init; }
}

public sealed record SkillCompilerContract
{
    public required SkillCompilerKind Kind { get; init; }

    public required string Version { get; init; }
}

public enum SkillCompilerKind
{
    [JsonStringEnumMemberName("deterministic-template")]
    DeterministicTemplate,
}

public sealed record SkillApplicabilityContract
{
    public required Guid RootGrantId { get; init; }

    public required SkillInvocation Invocation { get; init; }

    public required SkillMatchContract Match { get; init; }
}

public enum SkillInvocation
{
    Manual,
}

public sealed record SkillMatchContract
{
    public required bool RegularFilesOnly { get; init; }

    public string? OriginRelativeDirectory { get; init; }

    public IReadOnlyList<string>? Extensions { get; init; }

    public SkillFilenameMatchContract? Filename { get; init; }

    public long? MaxBytes { get; init; }
}

public sealed record SkillFilenameMatchContract
{
    public string? Prefix { get; init; }

    public string? Suffix { get; init; }

    public string? Contains { get; init; }

    public string? SafeRegex { get; init; }

    public bool CaseSensitive { get; init; }
}

public sealed record SkillVariableContract
{
    public required string Name { get; init; }

    public required SkillVariableSource Source { get; init; }

    public string? Argument { get; init; }

    public required IReadOnlyList<SkillVariableTransform> Transforms { get; init; }
}

public enum SkillVariableSource
{
    OriginalStem,
    OriginalExtension,
    RegexCapture,
    FileCreatedYear,
    FileCreatedMonth,
    FileModifiedYear,
    FileModifiedMonth,
    UserParameter,
}

public enum SkillVariableTransform
{
    Lowercase,
    Uppercase,
    SlugHyphen,
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "action")]
[JsonDerivedType(typeof(EnsureDirectoryStepContract), "ensure_directory")]
[JsonDerivedType(typeof(RenameFileStepContract), "rename_file")]
[JsonDerivedType(typeof(MoveFileStepContract), "move_file")]
[JsonDerivedType(typeof(CopyFileStepContract), "copy_file")]
public abstract record SkillStepContract
{
    public required string StepId { get; init; }
}

public sealed record EnsureDirectoryStepContract : SkillStepContract
{
    public required string DirectoryTemplate { get; init; }
}

public sealed record RenameFileStepContract : SkillStepContract
{
    public required string DestinationFileNameTemplate { get; init; }
}

public sealed record MoveFileStepContract : SkillStepContract
{
    public required string DestinationDirectoryTemplate { get; init; }

    public string? DestinationFileNameTemplate { get; init; }
}

public sealed record CopyFileStepContract : SkillStepContract
{
    public required string DestinationDirectoryTemplate { get; init; }

    public string? DestinationFileNameTemplate { get; init; }
}

public sealed record SkillPolicyContract
{
    public required CollisionPolicy Collision { get; init; }

    public required bool RequireExactPlanApproval { get; init; }

    public required bool AllowNetworkPaths { get; init; }

    public required bool AllowReparsePoints { get; init; }

    public required bool AllowOverwrite { get; init; }

    public required bool SameVolumeOnly { get; init; }
}

public enum CollisionPolicy
{
    Reject,
}

public sealed record SkillVerificationContract
{
    public required bool AllPlannedStepsVerified { get; init; }

    public required bool FailOnUnexpectedChange { get; init; }
}

public sealed record SkillProvenanceContract
{
    public required IReadOnlyList<Guid> TeachingEpisodeIds { get; init; }

    public required IReadOnlyList<Guid> ExampleIds { get; init; }

    public required IReadOnlyList<SkillUserAnswerContract> UserAnswers { get; init; }

    public int? ParentVersion { get; init; }
}

public sealed record SkillUserAnswerContract
{
    public required string QuestionCode { get; init; }

    public required string SelectedValue { get; init; }
}

public sealed record SkillCompatibilityContract
{
    public required string ContractVersion { get; init; }

    public required string MinimumExecutorVersion { get; init; }
}
