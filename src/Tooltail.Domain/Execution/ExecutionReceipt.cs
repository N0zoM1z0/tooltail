using System.Collections.ObjectModel;
using Tooltail.Domain.Common;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Domain.Execution;

public enum VerifiedEntryKind
{
    File,
    Directory,
}

public sealed record VerifiedEntryEvidence
{
    public VerifiedEntryEvidence(
        VerifiedEntryKind kind,
        string volumeIdentity,
        string entryIdentity,
        long? length,
        DateTimeOffset creationUtc,
        DateTimeOffset lastWriteUtc,
        int attributes,
        ContentHash? contentHash)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(volumeIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryIdentity);
        UtcGuard.RequireUtc(creationUtc, nameof(creationUtc));
        UtcGuard.RequireUtc(lastWriteUtc, nameof(lastWriteUtc));
        if ((kind == VerifiedEntryKind.File) != (length is not null))
        {
            throw new ArgumentException("Only verified file evidence has a length.", nameof(length));
        }

        if (length is not null)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(length.Value);
        }

        if (kind == VerifiedEntryKind.Directory && contentHash is not null)
        {
            throw new ArgumentException("Directory evidence cannot carry a content hash.", nameof(contentHash));
        }

        Kind = kind;
        VolumeIdentity = volumeIdentity;
        EntryIdentity = entryIdentity;
        Length = length;
        CreationUtc = creationUtc;
        LastWriteUtc = lastWriteUtc;
        Attributes = attributes;
        ContentHash = contentHash;
    }

    public VerifiedEntryKind Kind { get; }

    public string VolumeIdentity { get; }

    public string EntryIdentity { get; }

    public long? Length { get; }

    public DateTimeOffset CreationUtc { get; }

    public DateTimeOffset LastWriteUtc { get; }

    public int Attributes { get; }

    public ContentHash? ContentHash { get; }
}

public sealed record VerifiedStepEvidence
{
    public VerifiedStepEvidence(
        int stepSequence,
        FilePrimitive primitive,
        string? sourceRelativePath,
        string destinationRelativePath,
        bool destinationWasAbsent,
        VerifiedEntryEvidence destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stepSequence, 1);
        if (!Enum.IsDefined(primitive))
        {
            throw new ArgumentOutOfRangeException(nameof(primitive));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRelativePath);
        ArgumentNullException.ThrowIfNull(destination);
        if (sourceRelativePath is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceRelativePath);
        }

        if ((primitive == FilePrimitive.EnsureDirectory) != (sourceRelativePath is null))
        {
            throw new ArgumentException("Verified evidence source shape must match its primitive.", nameof(sourceRelativePath));
        }

        StepSequence = stepSequence;
        Primitive = primitive;
        SourceRelativePath = sourceRelativePath;
        DestinationRelativePath = destinationRelativePath;
        DestinationWasAbsent = destinationWasAbsent;
        Destination = destination;
    }

    public int StepSequence { get; }

    public FilePrimitive Primitive { get; }

    public string? SourceRelativePath { get; }

    public string DestinationRelativePath { get; }

    public bool DestinationWasAbsent { get; }

    public VerifiedEntryEvidence Destination { get; }
}

public sealed record ExecutionReceipt
{
    private ExecutionReceipt(
        ReceiptId id,
        ExecutionId executionId,
        PlanId planId,
        PlanFingerprint planFingerprint,
        DateTimeOffset completedUtc,
        int verifiedStepCount,
        DateTimeOffset? undoAvailableUntilUtc,
        IEnumerable<string> residualEffectCodes,
        IEnumerable<VerifiedStepEvidence> verifiedSteps)
    {
        Id = id;
        ExecutionId = executionId;
        PlanId = planId;
        PlanFingerprint = planFingerprint;
        CompletedUtc = completedUtc;
        VerifiedStepCount = verifiedStepCount;
        UndoAvailableUntilUtc = undoAvailableUntilUtc;
        ResidualEffectCodes = new ReadOnlyCollection<string>(residualEffectCodes.ToArray());
        VerifiedSteps = new ReadOnlyCollection<VerifiedStepEvidence>(verifiedSteps.ToArray());
    }

    public ReceiptId Id { get; }

    public ExecutionId ExecutionId { get; }

    public PlanId PlanId { get; }

    public PlanFingerprint PlanFingerprint { get; }

    public DateTimeOffset CompletedUtc { get; }

    public int VerifiedStepCount { get; }

    public DateTimeOffset? UndoAvailableUntilUtc { get; }

    public IReadOnlyList<string> ResidualEffectCodes { get; }

    public IReadOnlyList<VerifiedStepEvidence> VerifiedSteps { get; }

    public static DomainResult<ExecutionReceipt> CreateVerified(
        ReceiptId id,
        ExecutionJournal journal,
        DateTimeOffset completedUtc,
        DateTimeOffset? undoAvailableUntilUtc)
    {
        IdentifierGuard.NotEmpty(id.Value);
        ArgumentNullException.ThrowIfNull(journal);
        UtcGuard.RequireUtc(completedUtc, nameof(completedUtc));
        if (completedUtc < journal.Events[^1].OccurredUtc)
        {
            return DomainResult.Failure<ExecutionReceipt>(
                "receipt.completion_before_journal",
                "A receipt cannot complete before its journal.");
        }

        if (undoAvailableUntilUtc is not null)
        {
            UtcGuard.RequireUtc(undoAvailableUntilUtc.Value, nameof(undoAvailableUntilUtc));
            if (undoAvailableUntilUtc <= completedUtc)
            {
                return DomainResult.Failure<ExecutionReceipt>(
                    "receipt.undo_window_invalid",
                    "An undo window must end after receipt completion.");
            }
        }

        if (journal.Kind != ExecutionJournalKind.Standard)
        {
            return DomainResult.Failure<ExecutionReceipt>(
                "receipt.journal_kind_invalid",
                "A normal execution receipt requires a standard journal.");
        }

        for (int step = 1; step <= journal.OperationCount; step++)
        {
            if (journal.AssessStep(step).Status != StepRecoveryStatus.Verified)
            {
                return DomainResult.Failure<ExecutionReceipt>(
                    "receipt.execution_not_verified",
                    "A successful receipt requires every planned step to be verified.");
            }
        }

        return DomainResult.Success(
            new ExecutionReceipt(
                id,
                journal.ExecutionId,
                journal.PlanId,
                journal.PlanFingerprint,
                completedUtc,
                journal.OperationCount,
                undoAvailableUntilUtc,
                [],
                []));
    }

    public static DomainResult<ExecutionReceipt> CreateVerified(
        ReceiptId id,
        ExecutionPlan plan,
        ExecutionJournal journal,
        DateTimeOffset completedUtc,
        DateTimeOffset? undoAvailableUntilUtc,
        IEnumerable<VerifiedStepEvidence> verifiedSteps)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(verifiedSteps);
        if (journal.PlanId != plan.Definition.Id || journal.PlanFingerprint != plan.Fingerprint)
        {
            return DomainResult.Failure<ExecutionReceipt>(
                "receipt.plan_mismatch",
                "The receipt plan does not match the execution journal.");
        }

        DomainResult<ExecutionReceipt> basic = CreateVerified(
            id,
            journal,
            completedUtc,
            undoAvailableUntilUtc);
        if (!basic.IsSuccess)
        {
            return basic;
        }

        VerifiedStepEvidence[] materialized = verifiedSteps.ToArray();
        if (materialized.Length != plan.Definition.Operations.Count)
        {
            return DomainResult.Failure<ExecutionReceipt>(
                "receipt.evidence_incomplete",
                "A file execution receipt requires evidence for every verified step.");
        }

        for (int index = 0; index < materialized.Length; index++)
        {
            VerifiedStepEvidence evidence = materialized[index];
            PlannedFileOperation operation = plan.Definition.Operations[index];
            if (evidence.StepSequence != operation.Sequence ||
                evidence.Primitive != operation.Primitive ||
                !string.Equals(evidence.SourceRelativePath, operation.SourceRelativePath, StringComparison.Ordinal) ||
                !string.Equals(evidence.DestinationRelativePath, operation.DestinationRelativePath, StringComparison.Ordinal) ||
                evidence.DestinationWasAbsent !=
                (operation.DestinationPrecondition == DestinationPrecondition.Absent) ||
                (operation.Primitive == FilePrimitive.EnsureDirectory &&
                 evidence.Destination.Kind != VerifiedEntryKind.Directory) ||
                (operation.Primitive != FilePrimitive.EnsureDirectory &&
                 evidence.Destination.Kind != VerifiedEntryKind.File))
            {
                return DomainResult.Failure<ExecutionReceipt>(
                    "receipt.evidence_plan_mismatch",
                    "Verified step evidence must match the exact ordered plan.");
            }
        }

        ExecutionReceipt receipt = basic.Value!;
        return DomainResult.Success(
            new ExecutionReceipt(
                receipt.Id,
                receipt.ExecutionId,
                receipt.PlanId,
                receipt.PlanFingerprint,
                receipt.CompletedUtc,
                receipt.VerifiedStepCount,
                receipt.UndoAvailableUntilUtc,
                receipt.ResidualEffectCodes,
                materialized));
    }
}
