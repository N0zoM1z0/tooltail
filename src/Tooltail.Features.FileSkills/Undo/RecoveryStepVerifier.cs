using Tooltail.Application.Abstractions;
using Tooltail.Domain.Execution;
using Tooltail.Features.FileSkills.Snapshots;

namespace Tooltail.Features.FileSkills.Undo;

internal sealed record RecoveryStepVerification(
    bool IsSuccess,
    string ReasonCode,
    VerifiedRecoveryStepEvidence? Evidence)
{
    public static RecoveryStepVerification Success(VerifiedRecoveryStepEvidence evidence) =>
        new(true, "undo.step_verified", evidence);

    public static RecoveryStepVerification Failure(string reasonCode) =>
        new(false, reasonCode, null);
}

internal static class RecoveryStepVerifier
{
    public static RecoveryStepVerification Verify(
        FolderSnapshot before,
        FolderSnapshot after,
        PlannedRecoveryOperation operation)
        => VerifyCore(before, after, operation, mutationEvidence: null, requireMutationEvidence: false);

    public static RecoveryStepVerification Verify(
        FolderSnapshot before,
        FolderSnapshot after,
        PlannedRecoveryOperation operation,
        FileMutationEvidence? mutationEvidence) =>
        VerifyCore(before, after, operation, mutationEvidence, requireMutationEvidence: true);

    private static RecoveryStepVerification VerifyCore(
        FolderSnapshot before,
        FolderSnapshot after,
        PlannedRecoveryOperation operation,
        FileMutationEvidence? mutationEvidence,
        bool requireMutationEvidence)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(operation);
        if (!before.IsComplete || !after.IsComplete)
        {
            return RecoveryStepVerification.Failure("undo.verification_snapshot_incomplete");
        }

        if (before.RootIdentity != after.RootIdentity)
        {
            return RecoveryStepVerification.Failure("undo.verification_root_changed");
        }

        if (after.StartedUtc < before.CompletedUtc)
        {
            return RecoveryStepVerification.Failure("undo.verification_snapshot_overlap");
        }

        if (!TryIndex(before, out Dictionary<string, FolderSnapshotEntry>? beforeByPath) ||
            !TryIndex(after, out Dictionary<string, FolderSnapshotEntry>? afterByPath))
        {
            return RecoveryStepVerification.Failure("undo.verification_path_alias");
        }

        if (!beforeByPath!.TryGetValue(
                operation.SourceRelativePath,
                out FolderSnapshotEntry? source) ||
            !string.Equals(
                source.RelativePath,
                operation.SourceRelativePath,
                StringComparison.Ordinal) ||
            !MatchesEvidence(source, operation.ExpectedSource))
        {
            return RecoveryStepVerification.Failure("undo.source_changed");
        }

        RecoveryStepVerification verification =
            operation.Primitive == RecoveryPrimitive.RemoveCreatedEntry
            ? VerifyRemoval(beforeByPath, afterByPath!, operation, source)
            : VerifyRelocation(beforeByPath, afterByPath!, operation, source);
        if (!verification.IsSuccess || !requireMutationEvidence ||
            operation.Primitive == RecoveryPrimitive.RemoveCreatedEntry)
        {
            return verification;
        }

        VerifiedEntryEvidence recovered = verification.Evidence!.RecoveredEntry;
        return mutationEvidence is not null &&
            !mutationEvidence.DestinationCreatedByThisCall &&
            string.Equals(
                recovered.VolumeIdentity,
                mutationEvidence.VolumeIdentity,
                StringComparison.Ordinal) &&
            string.Equals(
                recovered.EntryIdentity,
                mutationEvidence.EntryIdentity,
                StringComparison.Ordinal)
            ? verification
            : RecoveryStepVerification.Failure("undo.mutation_evidence_mismatch");
    }

    private static RecoveryStepVerification VerifyRemoval(
        Dictionary<string, FolderSnapshotEntry> before,
        Dictionary<string, FolderSnapshotEntry> after,
        PlannedRecoveryOperation operation,
        FolderSnapshotEntry source)
    {
        if (source.Kind == SnapshotEntryKind.Directory &&
            before.Keys.Any(path => IsDescendant(path, operation.SourceRelativePath)))
        {
            return RecoveryStepVerification.Failure("undo.created_directory_not_empty");
        }

        if (after.ContainsKey(operation.SourceRelativePath))
        {
            return RecoveryStepVerification.Failure("undo.created_entry_removal_failed");
        }

        string? unexpected = FindUnexpectedChange(
            before,
            after,
            new HashSet<string>(
                [operation.SourceRelativePath],
                StringComparer.OrdinalIgnoreCase));
        return unexpected is null
            ? RecoveryStepVerification.Success(
                CreateEvidence(operation, operation.ExpectedSource))
            : RecoveryStepVerification.Failure(unexpected);
    }

    private static RecoveryStepVerification VerifyRelocation(
        Dictionary<string, FolderSnapshotEntry> before,
        Dictionary<string, FolderSnapshotEntry> after,
        PlannedRecoveryOperation operation,
        FolderSnapshotEntry source)
    {
        string destinationPath = operation.DestinationRelativePath!;
        if (before.ContainsKey(destinationPath) ||
            after.ContainsKey(operation.SourceRelativePath) ||
            !after.TryGetValue(destinationPath, out FolderSnapshotEntry? destination) ||
            !string.Equals(destination.RelativePath, destinationPath, StringComparison.Ordinal) ||
            !MatchesEvidence(destination, operation.ExpectedSource) ||
            !EquivalentEntryAtDifferentPath(source, destination))
        {
            return RecoveryStepVerification.Failure("undo.relocation_postcondition_failed");
        }

        string? unexpected = FindUnexpectedChange(
            before,
            after,
            new HashSet<string>(
                [operation.SourceRelativePath, destinationPath],
                StringComparer.OrdinalIgnoreCase));
        return unexpected is null
            ? RecoveryStepVerification.Success(
                CreateEvidence(operation, ToEvidence(destination)))
            : RecoveryStepVerification.Failure(unexpected);
    }

    private static VerifiedRecoveryStepEvidence CreateEvidence(
        PlannedRecoveryOperation operation,
        VerifiedEntryEvidence recoveredEntry) =>
        new(
            operation.Sequence,
            operation.OriginalStepSequence,
            operation.Primitive,
            operation.SourceRelativePath,
            operation.DestinationRelativePath,
            recoveredEntry);

    private static VerifiedEntryEvidence ToEvidence(FolderSnapshotEntry entry) =>
        new(
            entry.Kind == SnapshotEntryKind.File
                ? VerifiedEntryKind.File
                : VerifiedEntryKind.Directory,
            entry.VolumeIdentity!,
            entry.EntryIdentity!,
            entry.Length,
            entry.CreationUtc,
            entry.LastWriteUtc,
            (int)entry.Attributes,
            entry.ContentHashStatus == SnapshotContentHashStatus.Computed
                ? entry.ContentHash
                : null);

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

        return entry.Kind == SnapshotEntryKind.Directory ||
            (entry.Length == evidence.Length &&
             entry.CreationUtc == evidence.CreationUtc &&
             entry.LastWriteUtc == evidence.LastWriteUtc &&
             evidence.ContentHash is not null &&
             entry.ContentHashStatus == SnapshotContentHashStatus.Computed &&
             entry.ContentHash == evidence.ContentHash);
    }

    private static bool EquivalentEntryAtDifferentPath(
        FolderSnapshotEntry before,
        FolderSnapshotEntry after) =>
        before.Kind == after.Kind &&
        before.Length == after.Length &&
        before.CreationUtc == after.CreationUtc &&
        before.LastWriteUtc == after.LastWriteUtc &&
        before.Attributes == after.Attributes &&
        before.IsReparsePoint == after.IsReparsePoint &&
        string.Equals(before.VolumeIdentity, after.VolumeIdentity, StringComparison.Ordinal) &&
        string.Equals(before.EntryIdentity, after.EntryIdentity, StringComparison.Ordinal) &&
        before.ContentHashStatus == after.ContentHashStatus &&
        before.ContentHash == after.ContentHash;

    private static string? FindUnexpectedChange(
        Dictionary<string, FolderSnapshotEntry> before,
        Dictionary<string, FolderSnapshotEntry> after,
        HashSet<string> excluded)
    {
        FolderSnapshotEntry[] expected = before.Values
            .Where(entry => !excluded.Contains(entry.RelativePath))
            .ToArray();
        FolderSnapshotEntry[] actual = after.Values
            .Where(entry => !excluded.Contains(entry.RelativePath))
            .ToArray();
        if (expected.Length != actual.Length)
        {
            return "undo.unexpected_entry_set_changed";
        }

        foreach (FolderSnapshotEntry beforeEntry in expected)
        {
            if (!after.TryGetValue(beforeEntry.RelativePath, out FolderSnapshotEntry? afterEntry) ||
                !EquivalentUnchanged(beforeEntry, afterEntry))
            {
                return "undo.unexpected_entry_changed";
            }
        }

        return null;
    }

    private static bool EquivalentUnchanged(
        FolderSnapshotEntry before,
        FolderSnapshotEntry after)
    {
        if (!string.Equals(before.RelativePath, after.RelativePath, StringComparison.Ordinal) ||
            before.Kind != after.Kind ||
            before.Attributes != after.Attributes ||
            before.IsReparsePoint != after.IsReparsePoint ||
            !string.Equals(before.VolumeIdentity, after.VolumeIdentity, StringComparison.Ordinal) ||
            !string.Equals(before.EntryIdentity, after.EntryIdentity, StringComparison.Ordinal))
        {
            return false;
        }

        return before.Kind == SnapshotEntryKind.Directory ||
            (before.Length == after.Length &&
             before.CreationUtc == after.CreationUtc &&
             before.LastWriteUtc == after.LastWriteUtc &&
             before.ContentHashStatus == after.ContentHashStatus &&
             before.ContentHash == after.ContentHash);
    }

    private static bool TryIndex(
        FolderSnapshot snapshot,
        out Dictionary<string, FolderSnapshotEntry>? index)
    {
        index = new Dictionary<string, FolderSnapshotEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (FolderSnapshotEntry entry in snapshot.Entries)
        {
            if (!index.TryAdd(entry.RelativePath, entry))
            {
                index = null;
                return false;
            }
        }

        return true;
    }

    private static bool IsDescendant(string candidate, string ancestor) =>
        candidate.Length > ancestor.Length &&
        candidate.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase) &&
        candidate[ancestor.Length] == '\\';
}
