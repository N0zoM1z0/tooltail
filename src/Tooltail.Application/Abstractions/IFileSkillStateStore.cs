using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Domain.Teaching;

namespace Tooltail.Application.Abstractions;

public enum PersistedSnapshotStatus
{
    Complete,
    Incomplete,
    Invalid,
}

public enum PersistedPlanKind
{
    Standard,
    Recovery,
}

public sealed record CompanionStateRecord(
    CompanionId Id,
    string DisplayName,
    DateTimeOffset CreatedUtc,
    int IdentitySchemaVersion,
    string PresentationJson);

public sealed record LocalFolderGrantStateRecord(
    LocalFolderGrant Grant,
    byte[]? ProtectedCanonicalRoot);

public sealed record FolderSnapshotStateRecord(
    Guid SnapshotId,
    GrantId GrantId,
    ResourceRootIdentity RootIdentity,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    PersistedSnapshotStatus Status,
    string? ReasonCode,
    long HashedBytes,
    string SnapshotJson,
    DateTimeOffset? ExpiresUtc);

public sealed record DemonstrationExampleStateRecord(
    ExampleId Id,
    FilePrimitive EffectType,
    string? SourceRelativePath,
    string DestinationRelativePath,
    string? SourceFingerprintJson,
    string? UserLabel);

public sealed record TeachingEpisodeStateRecord(
    TeachingEpisode Episode,
    Guid? BaselineSnapshotId,
    Guid? FinalSnapshotId,
    string? ReconciliationSummaryJson,
    DateTimeOffset? RawEvidenceExpiryUtc,
    IReadOnlyList<DemonstrationExampleStateRecord> Examples);

public sealed record SkillVersionStateRecord(
    CompanionId CompanionId,
    string DisplayName,
    DateTimeOffset SkillCreatedUtc,
    SkillVersion Version,
    bool MakeCurrent,
    string SchemaVersion,
    string SkillSpecJson,
    string CompilerId,
    DateTimeOffset? ApprovedUtc,
    string? SemanticDiffJson);

public sealed record StoredPlanDocument(
    PlanId Id,
    PersistedPlanKind Kind,
    SkillId SkillId,
    SkillVersionNumber SkillVersion,
    GrantId GrantId,
    PlanFingerprint Fingerprint,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc,
    string ContractVersion,
    string CanonicalJson);

public sealed record StateWriteResult(bool IsSuccess, string? FailureCode)
{
    public static StateWriteResult Success { get; } = new(true, null);

    public static StateWriteResult Failure(string failureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        return new StateWriteResult(false, failureCode);
    }
}

public sealed record StateReadResult<T>(bool IsSuccess, string ReasonCode, T? Value);

public static class StateReadResult
{
    public static StateReadResult<T> Success<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new StateReadResult<T>(true, "persistence.read", value);
    }

    public static StateReadResult<T> Failure<T>(string reasonCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        return new StateReadResult<T>(false, reasonCode, default);
    }
}

public interface IFileSkillStateStore
{
    ValueTask<StateWriteResult> StoreCompanionAsync(
        CompanionStateRecord companion,
        CancellationToken cancellationToken = default);

    ValueTask<StateWriteResult> StoreLocalFolderGrantAsync(
        LocalFolderGrantStateRecord grant,
        CancellationToken cancellationToken = default);

    ValueTask<StateWriteResult> StoreFolderSnapshotAsync(
        FolderSnapshotStateRecord snapshot,
        CancellationToken cancellationToken = default);

    ValueTask<StateWriteResult> StoreTeachingEpisodeAsync(
        TeachingEpisodeStateRecord episode,
        CancellationToken cancellationToken = default);

    ValueTask<StateWriteResult> StoreSkillVersionAsync(
        SkillVersionStateRecord skillVersion,
        CancellationToken cancellationToken = default);

    ValueTask<StateWriteResult> StoreExecutionPlanAsync(
        ExecutionPlan plan,
        string canonicalJson,
        CancellationToken cancellationToken = default);

    ValueTask<StateWriteResult> StoreRecoveryPlanAsync(
        RecoveryPlan plan,
        string canonicalJson,
        CancellationToken cancellationToken = default);

    ValueTask<StateWriteResult> StoreApprovalAsync(
        PlanApproval approval,
        CancellationToken cancellationToken = default);

    ValueTask<StateReadResult<SkillVersionStateRecord>> LoadSkillVersionAsync(
        SkillId skillId,
        SkillVersionNumber version,
        CancellationToken cancellationToken = default);

    ValueTask<StateReadResult<StoredPlanDocument>> LoadPlanDocumentAsync(
        PlanId planId,
        CancellationToken cancellationToken = default);
}
