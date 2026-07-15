using Tooltail.Contracts.Json;
using Tooltail.Contracts.Skills;

namespace Tooltail.Contracts.Capsules;

public sealed record CompanionCapsuleContract : IVersionedContract
{
    public required string SchemaVersion { get; init; }

    public required Guid CapsuleId { get; init; }

    public required DateTimeOffset ExportedAt { get; init; }

    public required CapsuleProducerContract Producer { get; init; }

    public required CapsuleCompanionContract Companion { get; init; }

    public required IReadOnlyList<CapsuleSkillContract> Skills { get; init; }

    public required CapsuleContentPolicyContract ContentPolicy { get; init; }
}

public sealed record CapsuleProducerContract
{
    public required string Name { get; init; }

    public required string Version { get; init; }
}

public sealed record CapsuleCompanionContract
{
    public required Guid CompanionId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required string DisplayName { get; init; }

    public required CapsulePresentationContract Presentation { get; init; }
}

public sealed record CapsulePresentationContract
{
    public required string BodyStyle { get; init; }

    public required string Accent { get; init; }
}

public sealed record CapsuleSkillContract
{
    public required SkillSpecContract SkillSpec { get; init; }

    public required ExportedSkillLifecycleState ExportedLifecycleState { get; init; }

    public required CapsuleSourceGrantBindingContract SourceGrantBinding { get; init; }

    public required CapsuleEvidenceSummaryContract EvidenceSummary { get; init; }
}

public enum ExportedSkillLifecycleState
{
    Draft,
    Approved,
    Practiced,
    Reliable,
    Stale,
}

public sealed record CapsuleSourceGrantBindingContract
{
    public required Guid SourceGrantId { get; init; }

    public required CapsuleImportBehavior ImportBehavior { get; init; }
}

public enum CapsuleImportBehavior
{
    RequireUserRebind,
}

public sealed record CapsuleEvidenceSummaryContract
{
    public required int VerifiedSuccessCount { get; init; }

    public required int VerifiedFailureCount { get; init; }

    public required int CorrectionCount { get; init; }

    public DateTimeOffset? LastVerifiedAt { get; init; }
}

public sealed record CapsuleContentPolicyContract
{
    public required bool ContainsRawPaths { get; init; }

    public required bool ContainsRawFileNames { get; init; }

    public required bool ContainsFileContents { get; init; }

    public required bool ContainsModelTranscripts { get; init; }

    public required bool ContainsCredentials { get; init; }
}
