using System.Collections.ObjectModel;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Features.FileSkills.Execution;
using Tooltail.Features.FileSkills.Paths;

namespace Tooltail.Features.FileSkills.Undo;

public enum UndoExecutionStatus
{
    Verified,
    Cancelled,
    AuthorityDenied,
    PreconditionFailed,
    VerificationFailed,
    PersistenceFailed,
    RecoveryRequired,
}

public sealed record UndoExecutionRequest
{
    public UndoExecutionRequest(
        ExecutionId executionId,
        ReceiptId receiptId,
        RecoveryPlan plan,
        ExecutionAuthorization authorization,
        ExecutionPlan originalPlan,
        ExecutionJournal originalJournal,
        ExecutionReceipt originalReceipt,
        CanonicalLocalRoot root)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(originalPlan);
        ArgumentNullException.ThrowIfNull(originalJournal);
        ArgumentNullException.ThrowIfNull(originalReceipt);
        ArgumentNullException.ThrowIfNull(root);
        if (authorization.Purpose != ExecutionAuthorizationPurpose.Undo ||
            authorization.PlanId != plan.Definition.Id ||
            authorization.Fingerprint != plan.Fingerprint)
        {
            throw new ArgumentException(
                "Undo authorization must match the exact recovery plan.",
                nameof(authorization));
        }

        if (executionId == plan.Definition.OriginalExecutionId ||
            originalPlan.Definition.Id != plan.Definition.OriginalPlanId ||
            originalPlan.Fingerprint != plan.Definition.OriginalPlanFingerprint ||
            originalJournal.ExecutionId != plan.Definition.OriginalExecutionId ||
            originalJournal.PlanId != plan.Definition.OriginalPlanId ||
            originalJournal.PlanFingerprint != plan.Definition.OriginalPlanFingerprint ||
            originalReceipt.ExecutionId != plan.Definition.OriginalExecutionId ||
            originalReceipt.PlanId != plan.Definition.OriginalPlanId ||
            originalReceipt.PlanFingerprint != plan.Definition.OriginalPlanFingerprint ||
            !RecoveryProofMatches(plan, originalPlan, originalJournal, originalReceipt))
        {
            throw new ArgumentException(
                "Undo must link a distinct recovery execution to the exact original journal.",
                nameof(originalJournal));
        }

        if (root.Identity != plan.Definition.RootIdentity)
        {
            throw new ArgumentException(
                "The canonical root must match the recovery plan root.",
                nameof(root));
        }

        ExecutionId = executionId;
        ReceiptId = receiptId;
        Plan = plan;
        Authorization = authorization;
        OriginalPlan = originalPlan;
        OriginalJournal = originalJournal;
        OriginalReceipt = originalReceipt;
        Root = root;
    }

    public ExecutionId ExecutionId { get; }

    public ReceiptId ReceiptId { get; }

    public RecoveryPlan Plan { get; }

    public ExecutionAuthorization Authorization { get; }

    public ExecutionPlan OriginalPlan { get; }

    public ExecutionJournal OriginalJournal { get; }

    public ExecutionReceipt OriginalReceipt { get; }

    public CanonicalLocalRoot Root { get; }

    private static bool RecoveryProofMatches(
        RecoveryPlan recoveryPlan,
        ExecutionPlan originalPlan,
        ExecutionJournal originalJournal,
        ExecutionReceipt originalReceipt)
    {
        RecoveryPlanDefinition recoveryDefinition = recoveryPlan.Definition;
        ExecutionPlanDefinition originalDefinition = originalPlan.Definition;
        if (!CanonicalRecoveryPlan.HasValidFingerprint(recoveryPlan) ||
            !CanonicalExecutionPlan.HasValidFingerprint(originalPlan) ||
            originalJournal.Kind != ExecutionJournalKind.Standard ||
            originalJournal.OperationCount != originalDefinition.Operations.Count ||
            originalJournal.OperationPrimitives.Count != originalDefinition.Operations.Count ||
            originalJournal.OperationInverseKinds.Count != originalDefinition.Operations.Count ||
            originalReceipt.VerifiedStepCount != originalDefinition.Operations.Count ||
            originalReceipt.VerifiedSteps.Count != originalDefinition.Operations.Count ||
            originalReceipt.ResidualEffectCodes.Count != 0 ||
            originalReceipt.UndoAvailableUntilUtc is null ||
            originalReceipt.CompletedUtc < originalJournal.Events[^1].OccurredUtc ||
            recoveryDefinition.Id == originalDefinition.Id ||
            recoveryDefinition.SkillId != originalDefinition.SkillId ||
            recoveryDefinition.SkillVersion != originalDefinition.SkillVersion ||
            recoveryDefinition.SkillSpecificationHash !=
                originalDefinition.SkillSpecificationHash ||
            recoveryDefinition.GrantId != originalDefinition.GrantId ||
            recoveryDefinition.RootIdentity != originalDefinition.RootIdentity ||
            !recoveryDefinition.GrantedCapabilities.SetEquals(
                originalDefinition.GrantedCapabilities) ||
            recoveryDefinition.CreatedUtc < originalReceipt.CompletedUtc ||
            recoveryDefinition.CreatedUtc >= originalReceipt.UndoAvailableUntilUtc.Value ||
            recoveryDefinition.ExpiresUtc > originalReceipt.UndoAvailableUntilUtc.Value)
        {
            return false;
        }

        List<int> expectedRecoveredSteps = [];
        for (int index = 0; index < originalDefinition.Operations.Count; index++)
        {
            PlannedFileOperation original = originalDefinition.Operations[index];
            VerifiedStepEvidence evidence = originalReceipt.VerifiedSteps[index];
            JournalInverseKind expectedInverse = ExpectedInverse(original);
            if (originalJournal.OperationPrimitives[index] != original.Primitive ||
                originalJournal.OperationInverseKinds[index] != expectedInverse ||
                originalJournal.AssessStep(original.Sequence).Status !=
                    StepRecoveryStatus.Verified ||
                evidence.StepSequence != original.Sequence ||
                evidence.Primitive != original.Primitive ||
                !string.Equals(
                    evidence.SourceRelativePath,
                    original.SourceRelativePath,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    evidence.DestinationRelativePath,
                    original.DestinationRelativePath,
                    StringComparison.Ordinal) ||
                evidence.DestinationWasAbsent !=
                    (original.DestinationPrecondition == DestinationPrecondition.Absent))
            {
                return false;
            }

            if (expectedInverse != JournalInverseKind.None)
            {
                expectedRecoveredSteps.Add(original.Sequence);
            }
        }

        expectedRecoveredSteps.Reverse();
        if (recoveryDefinition.Operations.Count != expectedRecoveredSteps.Count)
        {
            return false;
        }

        for (int recoveryIndex = 0;
             recoveryIndex < recoveryDefinition.Operations.Count;
             recoveryIndex++)
        {
            PlannedRecoveryOperation recovery =
                recoveryDefinition.Operations[recoveryIndex];
            if (recovery.OriginalStepSequence != expectedRecoveredSteps[recoveryIndex])
            {
                return false;
            }

            int index = recovery.OriginalStepSequence - 1;
            PlannedFileOperation original = originalDefinition.Operations[index];
            VerifiedStepEvidence evidence = originalReceipt.VerifiedSteps[index];
            if (original.Sequence != recovery.OriginalStepSequence ||
                original.Primitive != recovery.OriginalPrimitive ||
                originalJournal.OperationInverseKinds[index] != ExpectedInverse(recovery.Primitive) ||
                evidence.StepSequence != original.Sequence ||
                evidence.Primitive != original.Primitive ||
                !evidence.DestinationWasAbsent ||
                evidence.Destination != recovery.ExpectedSource ||
                !string.Equals(
                    original.DestinationRelativePath,
                    recovery.SourceRelativePath,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    evidence.DestinationRelativePath,
                    recovery.SourceRelativePath,
                    StringComparison.Ordinal) ||
                !RecoveryDestinationMatchesOriginalSource(original, recovery) ||
                !string.Equals(
                    evidence.SourceRelativePath,
                    original.SourceRelativePath,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static JournalInverseKind ExpectedInverse(PlannedFileOperation operation) =>
        operation.Primitive switch
        {
            FilePrimitive.EnsureDirectory when
                operation.DestinationPrecondition == DestinationPrecondition.ExistingDirectory =>
                JournalInverseKind.None,
            FilePrimitive.EnsureDirectory => JournalInverseKind.RemoveCreatedEntry,
            FilePrimitive.RenameFile => JournalInverseKind.RenameBack,
            FilePrimitive.MoveFile => JournalInverseKind.MoveBack,
            FilePrimitive.CopyFile => JournalInverseKind.RemoveCreatedEntry,
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private static bool RecoveryDestinationMatchesOriginalSource(
        PlannedFileOperation original,
        PlannedRecoveryOperation recovery) =>
        recovery.Primitive == RecoveryPrimitive.RemoveCreatedEntry
            ? recovery.DestinationRelativePath is null
            : string.Equals(
                original.SourceRelativePath,
                recovery.DestinationRelativePath,
                StringComparison.Ordinal);

    private static JournalInverseKind ExpectedInverse(RecoveryPrimitive primitive) =>
        primitive switch
        {
            RecoveryPrimitive.RenameBack => JournalInverseKind.RenameBack,
            RecoveryPrimitive.MoveBack => JournalInverseKind.MoveBack,
            RecoveryPrimitive.RemoveCreatedEntry => JournalInverseKind.RemoveCreatedEntry,
            _ => throw new ArgumentOutOfRangeException(nameof(primitive)),
        };
}

public sealed record UndoExecutionResult
{
    internal UndoExecutionResult(
        UndoExecutionStatus status,
        string reasonCode,
        ExecutionJournal? recoveryJournal,
        ExecutionJournal originalJournal,
        RecoveryExecutionReceipt? receipt,
        int? failedStepSequence,
        IEnumerable<VerifiedRecoveryStepEvidence> verifiedSteps,
        IEnumerable<string> residualEffectCodes)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentNullException.ThrowIfNull(originalJournal);
        ArgumentNullException.ThrowIfNull(verifiedSteps);
        ArgumentNullException.ThrowIfNull(residualEffectCodes);
        Status = status;
        ReasonCode = reasonCode;
        RecoveryJournal = recoveryJournal;
        OriginalJournal = originalJournal;
        Receipt = receipt;
        FailedStepSequence = failedStepSequence;
        VerifiedSteps = new ReadOnlyCollection<VerifiedRecoveryStepEvidence>(
            verifiedSteps.ToArray());
        ResidualEffectCodes = new ReadOnlyCollection<string>(
            residualEffectCodes.ToArray());
    }

    public UndoExecutionStatus Status { get; }

    public string ReasonCode { get; }

    public ExecutionJournal? RecoveryJournal { get; }

    public ExecutionJournal OriginalJournal { get; }

    public RecoveryExecutionReceipt? Receipt { get; }

    public int? FailedStepSequence { get; }

    public IReadOnlyList<VerifiedRecoveryStepEvidence> VerifiedSteps { get; }

    public IReadOnlyList<string> ResidualEffectCodes { get; }

    public bool IsVerified =>
        Status == UndoExecutionStatus.Verified &&
        Receipt is not null &&
        ResidualEffectCodes.Count == 0;
}

public sealed record RecoveryExecutionBoundaryContext(
    ExecutionId ExecutionId,
    FileExecutionBoundary Boundary,
    int? StepSequence);

public interface IRecoveryExecutionFaultInjector
{
    void Reach(RecoveryExecutionBoundaryContext context);
}

public sealed class NoRecoveryExecutionFaultInjector : IRecoveryExecutionFaultInjector
{
    public static NoRecoveryExecutionFaultInjector Instance { get; } = new();

    private NoRecoveryExecutionFaultInjector()
    {
    }

    public void Reach(RecoveryExecutionBoundaryContext context) =>
        ArgumentNullException.ThrowIfNull(context);
}
