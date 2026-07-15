using System.Collections.ObjectModel;
using Tooltail.Domain.Common;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Domain.Execution;

public sealed record VerifiedRecoveryStepEvidence
{
    public VerifiedRecoveryStepEvidence(
        int stepSequence,
        int originalStepSequence,
        RecoveryPrimitive primitive,
        string sourceRelativePath,
        string? destinationRelativePath,
        VerifiedEntryEvidence recoveredEntry)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stepSequence, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(originalStepSequence, 1);
        if (!Enum.IsDefined(primitive))
        {
            throw new ArgumentOutOfRangeException(nameof(primitive));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRelativePath);
        ArgumentNullException.ThrowIfNull(recoveredEntry);
        bool relocation = primitive is RecoveryPrimitive.RenameBack or RecoveryPrimitive.MoveBack;
        if (relocation != (destinationRelativePath is not null))
        {
            throw new ArgumentException(
                "Only verified relocation recovery has a destination path.",
                nameof(destinationRelativePath));
        }

        if (destinationRelativePath is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationRelativePath);
        }

        StepSequence = stepSequence;
        OriginalStepSequence = originalStepSequence;
        Primitive = primitive;
        SourceRelativePath = sourceRelativePath;
        DestinationRelativePath = destinationRelativePath;
        RecoveredEntry = recoveredEntry;
    }

    public int StepSequence { get; }

    public int OriginalStepSequence { get; }

    public RecoveryPrimitive Primitive { get; }

    public string SourceRelativePath { get; }

    public string? DestinationRelativePath { get; }

    public VerifiedEntryEvidence RecoveredEntry { get; }
}

public sealed record RecoveryExecutionReceipt
{
    private RecoveryExecutionReceipt(
        ReceiptId id,
        ExecutionId executionId,
        PlanId planId,
        PlanFingerprint planFingerprint,
        ExecutionId originalExecutionId,
        PlanId originalPlanId,
        PlanFingerprint originalPlanFingerprint,
        DateTimeOffset completedUtc,
        IEnumerable<VerifiedRecoveryStepEvidence> verifiedSteps,
        IEnumerable<string> residualEffectCodes)
    {
        Id = id;
        ExecutionId = executionId;
        PlanId = planId;
        PlanFingerprint = planFingerprint;
        OriginalExecutionId = originalExecutionId;
        OriginalPlanId = originalPlanId;
        OriginalPlanFingerprint = originalPlanFingerprint;
        CompletedUtc = completedUtc;
        VerifiedSteps = new ReadOnlyCollection<VerifiedRecoveryStepEvidence>(
            verifiedSteps.ToArray());
        ResidualEffectCodes = new ReadOnlyCollection<string>(
            residualEffectCodes.ToArray());
    }

    public ReceiptId Id { get; }

    public ExecutionId ExecutionId { get; }

    public PlanId PlanId { get; }

    public PlanFingerprint PlanFingerprint { get; }

    public ExecutionId OriginalExecutionId { get; }

    public PlanId OriginalPlanId { get; }

    public PlanFingerprint OriginalPlanFingerprint { get; }

    public DateTimeOffset CompletedUtc { get; }

    public IReadOnlyList<VerifiedRecoveryStepEvidence> VerifiedSteps { get; }

    public IReadOnlyList<string> ResidualEffectCodes { get; }

    public static DomainResult<RecoveryExecutionReceipt> CreateVerified(
        ReceiptId id,
        RecoveryPlan plan,
        ExecutionJournal recoveryJournal,
        ExecutionJournal originalJournal,
        DateTimeOffset completedUtc,
        IEnumerable<VerifiedRecoveryStepEvidence> verifiedSteps)
    {
        IdentifierGuard.NotEmpty(id.Value);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(recoveryJournal);
        ArgumentNullException.ThrowIfNull(originalJournal);
        ArgumentNullException.ThrowIfNull(verifiedSteps);
        UtcGuard.RequireUtc(completedUtc, nameof(completedUtc));

        RecoveryPlanDefinition definition = plan.Definition;
        if (recoveryJournal.Kind != ExecutionJournalKind.Recovery ||
            recoveryJournal.PlanId != definition.Id ||
            recoveryJournal.PlanFingerprint != plan.Fingerprint ||
            originalJournal.ExecutionId != definition.OriginalExecutionId ||
            originalJournal.PlanId != definition.OriginalPlanId ||
            originalJournal.PlanFingerprint != definition.OriginalPlanFingerprint)
        {
            return Failure(
                "recovery_receipt.plan_mismatch",
                "Recovery receipt inputs must match both exact journaled plans.");
        }

        if (completedUtc < recoveryJournal.Events[^1].OccurredUtc ||
            completedUtc < originalJournal.Events[^1].OccurredUtc)
        {
            return Failure(
                "recovery_receipt.completion_before_journal",
                "A recovery receipt cannot complete before either linked journal.");
        }

        VerifiedRecoveryStepEvidence[] materialized = verifiedSteps
            .Take(definition.Operations.Count + 1)
            .ToArray();
        if (materialized.Length != definition.Operations.Count)
        {
            return Failure(
                "recovery_receipt.evidence_incomplete",
                "Every recovery operation requires exact verified evidence.");
        }

        for (int index = 0; index < definition.Operations.Count; index++)
        {
            PlannedRecoveryOperation operation = definition.Operations[index];
            VerifiedRecoveryStepEvidence evidence = materialized[index];
            if (recoveryJournal.AssessStep(operation.Sequence).Status !=
                    StepRecoveryStatus.Verified ||
                originalJournal.AssessStep(operation.OriginalStepSequence).Status !=
                    StepRecoveryStatus.RolledBack ||
                !HasRecoveryLink(
                    originalJournal,
                    operation.OriginalStepSequence,
                    recoveryJournal.ExecutionId) ||
                evidence.StepSequence != operation.Sequence ||
                evidence.OriginalStepSequence != operation.OriginalStepSequence ||
                evidence.Primitive != operation.Primitive ||
                !string.Equals(
                    evidence.SourceRelativePath,
                    operation.SourceRelativePath,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    evidence.DestinationRelativePath,
                    operation.DestinationRelativePath,
                    StringComparison.Ordinal) ||
                evidence.RecoveredEntry != operation.ExpectedSource)
            {
                return Failure(
                    "recovery_receipt.evidence_plan_mismatch",
                    "Recovery evidence and rollback links must match every exact plan step.");
            }
        }

        return DomainResult.Success(
            new RecoveryExecutionReceipt(
                id,
                recoveryJournal.ExecutionId,
                definition.Id,
                plan.Fingerprint,
                definition.OriginalExecutionId,
                definition.OriginalPlanId,
                definition.OriginalPlanFingerprint,
                completedUtc,
                materialized,
                []));
    }

    public static DomainResult<RecoveryExecutionReceipt> RehydrateVerified(
        ReceiptId id,
        ExecutionJournal recoveryJournal,
        ExecutionJournal originalJournal,
        ExecutionId originalExecutionId,
        PlanId originalPlanId,
        PlanFingerprint originalPlanFingerprint,
        DateTimeOffset completedUtc,
        IEnumerable<VerifiedRecoveryStepEvidence> verifiedSteps,
        IEnumerable<string> residualEffectCodes)
    {
        IdentifierGuard.NotEmpty(id.Value);
        ArgumentNullException.ThrowIfNull(recoveryJournal);
        ArgumentNullException.ThrowIfNull(originalJournal);
        IdentifierGuard.NotEmpty(originalExecutionId.Value);
        IdentifierGuard.NotEmpty(originalPlanId.Value);
        ArgumentNullException.ThrowIfNull(originalPlanFingerprint);
        ArgumentNullException.ThrowIfNull(verifiedSteps);
        ArgumentNullException.ThrowIfNull(residualEffectCodes);
        UtcGuard.RequireUtc(completedUtc, nameof(completedUtc));

        VerifiedRecoveryStepEvidence[] evidence = verifiedSteps
            .Take(recoveryJournal.OperationCount + 1)
            .ToArray();
        string[] residuals = residualEffectCodes.Take(1).ToArray();
        if (recoveryJournal.Kind != ExecutionJournalKind.Recovery ||
            originalJournal.Kind != ExecutionJournalKind.Standard ||
            recoveryJournal.ExecutionId == originalExecutionId ||
            originalJournal.ExecutionId != originalExecutionId ||
            originalJournal.PlanId != originalPlanId ||
            originalJournal.PlanFingerprint != originalPlanFingerprint ||
            completedUtc < recoveryJournal.Events[^1].OccurredUtc ||
            completedUtc < originalJournal.Events[^1].OccurredUtc ||
            evidence.Length != recoveryJournal.OperationCount ||
            residuals.Length != 0)
        {
            return Failure(
                "recovery_receipt.rehydrate_invalid",
                "Persisted recovery receipt state does not match its linked journals.");
        }

        for (int index = 0; index < evidence.Length; index++)
        {
            VerifiedRecoveryStepEvidence step = evidence[index];
            int originalStep = recoveryJournal.RecoveryOriginalStepSequences[index];
            if (originalStep > originalJournal.OperationCount ||
                recoveryJournal.AssessStep(index + 1).Status !=
                    StepRecoveryStatus.Verified ||
                originalJournal.AssessStep(originalStep).Status !=
                    StepRecoveryStatus.RolledBack ||
                !HasRecoveryLink(originalJournal, originalStep, recoveryJournal.ExecutionId) ||
                step.StepSequence != index + 1 ||
                step.OriginalStepSequence != originalStep ||
                step.Primitive != recoveryJournal.RecoveryOperationPrimitives[index])
            {
                return Failure(
                    "recovery_receipt.rehydrate_evidence_invalid",
                    "Persisted recovery evidence does not match both journal projections.");
            }
        }

        return DomainResult.Success(
            new RecoveryExecutionReceipt(
                id,
                recoveryJournal.ExecutionId,
                recoveryJournal.PlanId,
                recoveryJournal.PlanFingerprint,
                originalExecutionId,
                originalPlanId,
                originalPlanFingerprint,
                completedUtc,
                evidence,
                residuals));
    }

    private static bool HasRecoveryLink(
        ExecutionJournal originalJournal,
        int originalStepSequence,
        ExecutionId recoveryExecutionId) =>
        originalJournal.Events.OfType<StepRolledBackEvent>().Any(
            journalEvent =>
                journalEvent.StepSequence == originalStepSequence &&
                journalEvent.RecoveryExecutionId == recoveryExecutionId);

    private static DomainResult<RecoveryExecutionReceipt> Failure(
        string code,
        string message) =>
        DomainResult.Failure<RecoveryExecutionReceipt>(code, message);
}
