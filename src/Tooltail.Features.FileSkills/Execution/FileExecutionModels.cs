using System.Collections.ObjectModel;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Paths;

namespace Tooltail.Features.FileSkills.Execution;

public enum FileExecutionMode
{
    Production,
    Rehearsal,
}

public enum FileExecutionStatus
{
    Verified,
    Cancelled,
    AuthorityDenied,
    PreconditionFailed,
    VerificationFailed,
    PersistenceFailed,
    RecoveryRequired,
}

public sealed record FileExecutionRequest
{
    public FileExecutionRequest(
        ExecutionId executionId,
        ReceiptId receiptId,
        ExecutionPlan plan,
        ExecutionAuthorization authorization,
        CanonicalLocalRoot root,
        FileExecutionMode mode,
        TimeSpan? undoWindow = null)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(root);
        if (authorization.PlanId != plan.Definition.Id ||
            authorization.Fingerprint != plan.Fingerprint)
        {
            throw new ArgumentException("The execution authorization must match the exact plan.", nameof(authorization));
        }

        ExecutionAuthorizationPurpose requiredPurpose = mode == FileExecutionMode.Rehearsal
            ? ExecutionAuthorizationPurpose.Rehearsal
            : ExecutionAuthorizationPurpose.Production;
        if (authorization.Purpose != requiredPurpose)
        {
            throw new ArgumentException(
                "The authorization purpose must match the execution mode.",
                nameof(authorization));
        }

        if (root.Identity != plan.Definition.RootIdentity)
        {
            throw new ArgumentException("The canonical root must match the plan root identity.", nameof(root));
        }

        if (undoWindow is not null && undoWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(undoWindow));
        }

        ExecutionId = executionId;
        ReceiptId = receiptId;
        Plan = plan;
        Authorization = authorization;
        Root = root;
        Mode = mode;
        UndoWindow = undoWindow;
    }

    public ExecutionId ExecutionId { get; }

    public ReceiptId ReceiptId { get; }

    public ExecutionPlan Plan { get; }

    public ExecutionAuthorization Authorization { get; }

    public CanonicalLocalRoot Root { get; }

    public FileExecutionMode Mode { get; }

    public TimeSpan? UndoWindow { get; }
}

public sealed record FileExecutionResult
{
    internal FileExecutionResult(
        FileExecutionStatus status,
        string reasonCode,
        FileExecutionMode mode,
        ExecutionJournal? journal,
        ExecutionReceipt? receipt,
        int? failedStepSequence,
        IEnumerable<VerifiedStepEvidence> verifiedSteps)
    {
        if (!Enum.IsDefined(status) || !Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentNullException.ThrowIfNull(verifiedSteps);
        VerifiedStepEvidence[] materialized = verifiedSteps.ToArray();
        Status = status;
        ReasonCode = reasonCode;
        Mode = mode;
        Journal = journal;
        Receipt = receipt;
        FailedStepSequence = failedStepSequence;
        VerifiedSteps = new ReadOnlyCollection<VerifiedStepEvidence>(materialized);
    }

    public FileExecutionStatus Status { get; }

    public string ReasonCode { get; }

    public FileExecutionMode Mode { get; }

    public ExecutionJournal? Journal { get; }

    public ExecutionReceipt? Receipt { get; }

    public int? FailedStepSequence { get; }

    public IReadOnlyList<VerifiedStepEvidence> VerifiedSteps { get; }

    public bool IsVerified => Status == FileExecutionStatus.Verified && Receipt is not null;
}

public sealed record ExecutionAuthorityState(
    SkillVersion SkillVersion,
    LocalFolderGrant Grant);

public interface IExecutionAuthoritySource
{
    ValueTask<ExecutionAuthorityState?> ReadCurrentAsync(
        SkillId skillId,
        SkillVersionNumber skillVersion,
        GrantId grantId,
        CancellationToken cancellationToken = default);
}

public enum FileExecutionBoundary
{
    JournalOpened,
    StepIntentPersisted,
    BeforePrimitive,
    AfterPrimitive,
    MutationObservedPersisted,
    StepCommittedPersisted,
    StepVerifiedPersisted,
    OriginalStepRollbackLinked,
    StepFailedPersisted,
    RecoveryRequiredPersisted,
    ReceiptPersisted,
}

public sealed record FileExecutionBoundaryContext(
    ExecutionId ExecutionId,
    FileExecutionMode Mode,
    FileExecutionBoundary Boundary,
    int? StepSequence);

/// <summary>
/// Test-only fault boundary used by crash-prefix and race regression suites.
/// Production composition uses <see cref="NoFileExecutionFaultInjector"/>.
/// </summary>
public interface IFileExecutionFaultInjector
{
    void Reach(FileExecutionBoundaryContext context);
}

public sealed class NoFileExecutionFaultInjector : IFileExecutionFaultInjector
{
    public static NoFileExecutionFaultInjector Instance { get; } = new();

    private NoFileExecutionFaultInjector()
    {
    }

    public void Reach(FileExecutionBoundaryContext context) =>
        ArgumentNullException.ThrowIfNull(context);
}

public sealed record FileExecutionLimits
{
    public FileExecutionLimits(long maximumSourceFileBytes, TimeSpan maximumDuration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumSourceFileBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumDuration, TimeSpan.Zero);
        MaximumSourceFileBytes = maximumSourceFileBytes;
        MaximumDuration = maximumDuration;
    }

    public long MaximumSourceFileBytes { get; }

    public TimeSpan MaximumDuration { get; }

    public static FileExecutionLimits Default { get; } = new(
        maximumSourceFileBytes: 64 * 1024 * 1024,
        maximumDuration: TimeSpan.FromMinutes(2));
}
