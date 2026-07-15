using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Execution;
using Tooltail.Features.FileSkills.Snapshots;

namespace Tooltail.Features.FileSkills.Undo;

public enum UndoPlanningStatus
{
    Ready,
    InvalidEvidence,
    UndoExpired,
    AlreadyUndone,
    AuthorityMismatch,
    SnapshotInvalid,
    Conflict,
    NoEffects,
    LimitExceeded,
}

public sealed record UndoPlanningRequest
{
    public UndoPlanningRequest(
        PlanId recoveryPlanId,
        ExecutionPlan originalPlan,
        ExecutionJournal originalJournal,
        ExecutionReceipt originalReceipt,
        SkillVersion skillVersion,
        LocalFolderGrant grant,
        FolderSnapshot currentSnapshot,
        DateTimeOffset createdUtc,
        DateTimeOffset expiresUtc)
    {
        ArgumentNullException.ThrowIfNull(originalPlan);
        ArgumentNullException.ThrowIfNull(originalJournal);
        ArgumentNullException.ThrowIfNull(originalReceipt);
        ArgumentNullException.ThrowIfNull(skillVersion);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        RecoveryPlanId = recoveryPlanId;
        OriginalPlan = originalPlan;
        OriginalJournal = originalJournal;
        OriginalReceipt = originalReceipt;
        SkillVersion = skillVersion;
        Grant = grant;
        CurrentSnapshot = currentSnapshot;
        CreatedUtc = createdUtc;
        ExpiresUtc = expiresUtc;
    }

    public PlanId RecoveryPlanId { get; }

    public ExecutionPlan OriginalPlan { get; }

    public ExecutionJournal OriginalJournal { get; }

    public ExecutionReceipt OriginalReceipt { get; }

    public SkillVersion SkillVersion { get; }

    public LocalFolderGrant Grant { get; }

    public FolderSnapshot CurrentSnapshot { get; }

    public DateTimeOffset CreatedUtc { get; }

    public DateTimeOffset ExpiresUtc { get; }
}

public sealed record UndoPlanningResult(
    UndoPlanningStatus Status,
    string ReasonCode,
    RecoveryPlan? Plan,
    int RecoveryOperationCount)
{
    public bool IsReady => Status == UndoPlanningStatus.Ready && Plan is not null;
}

public sealed class UndoPlanner
{
    private readonly int maximumOperations;

    public UndoPlanner(int maximumOperations = 500)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumOperations, 1);
        this.maximumOperations = maximumOperations;
    }

    public UndoPlanningResult Plan(UndoPlanningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? evidenceFailure = ValidateEvidence(request);
        if (evidenceFailure is not null)
        {
            UndoPlanningStatus status = evidenceFailure switch
            {
                "undo.window_expired" or "undo.window_unavailable" =>
                    UndoPlanningStatus.UndoExpired,
                "undo.already_applied" => UndoPlanningStatus.AlreadyUndone,
                "undo.authority_mismatch" => UndoPlanningStatus.AuthorityMismatch,
                "undo.snapshot_invalid" => UndoPlanningStatus.SnapshotInvalid,
                _ => UndoPlanningStatus.InvalidEvidence,
            };
            return Failure(status, evidenceFailure);
        }

        if (!TryIndex(
                request.CurrentSnapshot,
                out Dictionary<string, FolderSnapshotEntry>? simulated))
        {
            return Failure(
                UndoPlanningStatus.SnapshotInvalid,
                "undo.snapshot_path_alias");
        }

        List<PlannedRecoveryOperation> operations = [];
        IReadOnlyList<PlannedFileOperation> originalOperations =
            request.OriginalPlan.Definition.Operations;
        IReadOnlyList<VerifiedStepEvidence> verifiedSteps =
            request.OriginalReceipt.VerifiedSteps;
        for (int index = originalOperations.Count - 1; index >= 0; index--)
        {
            PlannedFileOperation original = originalOperations[index];
            VerifiedStepEvidence evidence = verifiedSteps[index];
            JournalInverseKind inverse = request.OriginalJournal.OperationInverseKinds[index];
            if (inverse == JournalInverseKind.None)
            {
                continue;
            }

            if (operations.Count >= maximumOperations)
            {
                return Failure(
                    UndoPlanningStatus.LimitExceeded,
                    "undo.operation_limit_exceeded",
                    operations.Count);
            }

            PlannedRecoveryOperation? recovery = CreateRecoveryOperation(
                operations.Count + 1,
                original,
                evidence,
                inverse);
            if (recovery is null)
            {
                return Failure(
                    UndoPlanningStatus.InvalidEvidence,
                    "undo.inverse_proof_invalid",
                    operations.Count);
            }

            string? conflict = ValidateAndSimulate(recovery, simulated!);
            if (conflict is not null)
            {
                return Failure(
                    UndoPlanningStatus.Conflict,
                    conflict,
                    operations.Count);
            }

            operations.Add(recovery);
        }

        if (operations.Count == 0)
        {
            return Failure(
                UndoPlanningStatus.NoEffects,
                "undo.no_effects_to_recover");
        }

        ExecutionPlanDefinition originalDefinition = request.OriginalPlan.Definition;
        RecoveryPlanDefinition definition;
        try
        {
            definition = new RecoveryPlanDefinition(
                request.RecoveryPlanId,
                request.OriginalJournal.ExecutionId,
                originalDefinition.Id,
                request.OriginalPlan.Fingerprint,
                originalDefinition.SkillId,
                originalDefinition.SkillVersion,
                originalDefinition.SkillSpecificationHash,
                originalDefinition.GrantId,
                originalDefinition.RootIdentity,
                originalDefinition.GrantedCapabilities,
                request.CreatedUtc,
                request.ExpiresUtc,
                operations);
        }
        catch (ArgumentException)
        {
            return Failure(
                UndoPlanningStatus.InvalidEvidence,
                "undo.plan_definition_invalid",
                operations.Count);
        }

        var canonical = CanonicalRecoveryPlan.Create(definition);
        return canonical.IsSuccess
            ? new UndoPlanningResult(
                UndoPlanningStatus.Ready,
                "undo.plan_ready",
                canonical.Value,
                operations.Count)
            : Failure(
                UndoPlanningStatus.InvalidEvidence,
                canonical.Error!.Code,
                operations.Count);
    }

    private static string? ValidateEvidence(UndoPlanningRequest request)
    {
        if (request.CreatedUtc.Offset != TimeSpan.Zero ||
            request.ExpiresUtc.Offset != TimeSpan.Zero ||
            request.ExpiresUtc <= request.CreatedUtc)
        {
            return "undo.plan_time_invalid";
        }

        ExecutionPlan plan = request.OriginalPlan;
        ExecutionPlanDefinition definition = plan.Definition;
        ExecutionJournal journal = request.OriginalJournal;
        ExecutionReceipt receipt = request.OriginalReceipt;
        if (!CanonicalExecutionPlan.HasValidFingerprint(plan) ||
            journal.Kind != ExecutionJournalKind.Standard ||
            journal.ExecutionId != receipt.ExecutionId ||
            journal.PlanId != definition.Id ||
            journal.PlanFingerprint != plan.Fingerprint ||
            receipt.PlanId != definition.Id ||
            receipt.PlanFingerprint != plan.Fingerprint ||
            receipt.VerifiedStepCount != definition.Operations.Count ||
            receipt.VerifiedSteps.Count != definition.Operations.Count)
        {
            return "undo.original_evidence_mismatch";
        }

        if (receipt.UndoAvailableUntilUtc is null)
        {
            return "undo.window_unavailable";
        }

        if (request.CreatedUtc < receipt.CompletedUtc ||
            request.CreatedUtc >= receipt.UndoAvailableUntilUtc ||
            request.ExpiresUtc > receipt.UndoAvailableUntilUtc)
        {
            return "undo.window_expired";
        }

        if (request.SkillVersion.SkillId != definition.SkillId ||
            request.SkillVersion.Number != definition.SkillVersion ||
            !string.Equals(
                request.SkillVersion.SpecificationHash,
                definition.SkillSpecificationHash.Value,
                StringComparison.Ordinal) ||
            request.Grant.Id != definition.GrantId ||
            request.Grant.RootIdentity != definition.RootIdentity ||
            !request.Grant.Capabilities.SetEquals(definition.GrantedCapabilities))
        {
            return "undo.authority_mismatch";
        }

        if (!request.CurrentSnapshot.IsComplete ||
            request.CurrentSnapshot.RootIdentity != definition.RootIdentity ||
            request.CurrentSnapshot.CompletedUtc > request.CreatedUtc)
        {
            return "undo.snapshot_invalid";
        }

        for (int index = 0; index < definition.Operations.Count; index++)
        {
            StepRecoveryStatus status = journal.AssessStep(index + 1).Status;
            if (status == StepRecoveryStatus.RolledBack)
            {
                return "undo.already_applied";
            }

            PlannedFileOperation operation = definition.Operations[index];
            VerifiedStepEvidence evidence = receipt.VerifiedSteps[index];
            if (status != StepRecoveryStatus.Verified ||
                evidence.StepSequence != operation.Sequence ||
                evidence.Primitive != operation.Primitive ||
                !string.Equals(
                    evidence.SourceRelativePath,
                    operation.SourceRelativePath,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    evidence.DestinationRelativePath,
                    operation.DestinationRelativePath,
                    StringComparison.Ordinal) ||
                evidence.DestinationWasAbsent !=
                    (operation.DestinationPrecondition == DestinationPrecondition.Absent))
            {
                return "undo.original_evidence_mismatch";
            }
        }

        return null;
    }

    private static PlannedRecoveryOperation? CreateRecoveryOperation(
        int sequence,
        PlannedFileOperation original,
        VerifiedStepEvidence evidence,
        JournalInverseKind inverse)
    {
        if (!evidence.DestinationWasAbsent)
        {
            return null;
        }

        RecoveryPrimitive primitive;
        string? destination;
        switch (inverse)
        {
            case JournalInverseKind.RenameBack
                when original.Primitive == FilePrimitive.RenameFile:
                primitive = RecoveryPrimitive.RenameBack;
                destination = original.SourceRelativePath;
                break;
            case JournalInverseKind.MoveBack
                when original.Primitive == FilePrimitive.MoveFile:
                primitive = RecoveryPrimitive.MoveBack;
                destination = original.SourceRelativePath;
                break;
            case JournalInverseKind.RemoveCreatedEntry
                when original.Primitive is FilePrimitive.CopyFile or FilePrimitive.EnsureDirectory:
                primitive = RecoveryPrimitive.RemoveCreatedEntry;
                destination = null;
                break;
            default:
                return null;
        }

        try
        {
            return new PlannedRecoveryOperation(
                sequence,
                original.Sequence,
                original.Primitive,
                primitive,
                original.DestinationRelativePath,
                destination,
                evidence.Destination,
                evidence.DestinationWasAbsent);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? ValidateAndSimulate(
        PlannedRecoveryOperation operation,
        Dictionary<string, FolderSnapshotEntry> entries)
    {
        if (!entries.TryGetValue(operation.SourceRelativePath, out FolderSnapshotEntry? source) ||
            !string.Equals(
                source.RelativePath,
                operation.SourceRelativePath,
                StringComparison.Ordinal) ||
            !MatchesEvidence(source, operation.ExpectedSource))
        {
            return "undo.source_changed";
        }

        if (!ParentsExist(operation.SourceRelativePath, entries))
        {
            return "undo.source_parent_invalid";
        }

        if (operation.Primitive == RecoveryPrimitive.RemoveCreatedEntry)
        {
            if (source.Kind == SnapshotEntryKind.Directory &&
                entries.Keys.Any(path => IsDescendant(path, source.RelativePath)))
            {
                return "undo.created_directory_not_empty";
            }

            entries.Remove(source.RelativePath);
            return null;
        }

        string destination = operation.DestinationRelativePath!;
        if (entries.ContainsKey(destination))
        {
            return "undo.destination_not_absent";
        }

        if (!ParentsExist(destination, entries))
        {
            return "undo.destination_parent_invalid";
        }

        entries.Remove(source.RelativePath);
        entries.Add(destination, CopyAtPath(source, destination));
        return null;
    }

    private static bool MatchesEvidence(
        FolderSnapshotEntry entry,
        VerifiedEntryEvidence evidence)
    {
        if (entry.IsReparsePoint ||
            (entry.Kind == SnapshotEntryKind.File) !=
                (evidence.Kind == VerifiedEntryKind.File) ||
            !string.Equals(entry.VolumeIdentity, evidence.VolumeIdentity, StringComparison.Ordinal) ||
            !string.Equals(entry.EntryIdentity, evidence.EntryIdentity, StringComparison.Ordinal) ||
            (int)entry.Attributes != evidence.Attributes)
        {
            return false;
        }

        if (entry.Kind == SnapshotEntryKind.Directory)
        {
            return true;
        }

        return evidence.ContentHash is not null &&
            entry.ContentHashStatus == SnapshotContentHashStatus.Computed &&
            entry.CreationUtc == evidence.CreationUtc &&
            entry.Length == evidence.Length &&
            entry.LastWriteUtc == evidence.LastWriteUtc &&
            entry.ContentHash == evidence.ContentHash;
    }

    private static bool ParentsExist(
        string path,
        Dictionary<string, FolderSnapshotEntry> entries)
    {
        int separator = path.LastIndexOf('\\');
        if (separator < 0)
        {
            return true;
        }

        string current = string.Empty;
        foreach (string segment in path[..separator].Split('\\'))
        {
            current = current.Length == 0 ? segment : $"{current}\\{segment}";
            if (!entries.TryGetValue(current, out FolderSnapshotEntry? parent) ||
                parent.Kind != SnapshotEntryKind.Directory ||
                parent.IsReparsePoint)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryIndex(
        FolderSnapshot snapshot,
        out Dictionary<string, FolderSnapshotEntry>? entries)
    {
        entries = new Dictionary<string, FolderSnapshotEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (FolderSnapshotEntry entry in snapshot.Entries)
        {
            if (!entries.TryAdd(entry.RelativePath, entry))
            {
                entries = null;
                return false;
            }
        }

        return true;
    }

    private static FolderSnapshotEntry CopyAtPath(
        FolderSnapshotEntry source,
        string path) =>
        new(
            path,
            source.Kind,
            source.Length,
            source.CreationUtc,
            source.LastWriteUtc,
            source.Attributes,
            source.IsReparsePoint,
            source.VolumeIdentity,
            source.EntryIdentity,
            source.ContentHashStatus,
            source.ContentHash);

    private static bool IsDescendant(string candidate, string ancestor) =>
        candidate.Length > ancestor.Length &&
        candidate.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase) &&
        candidate[ancestor.Length] == '\\';

    private static UndoPlanningResult Failure(
        UndoPlanningStatus status,
        string reasonCode,
        int operationCount = 0) =>
        new(status, reasonCode, null, operationCount);
}
