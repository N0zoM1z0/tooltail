using Tooltail.Application.Abstractions;
using Tooltail.Domain.Execution;
using Tooltail.Features.FileSkills.Snapshots;

namespace Tooltail.Features.FileSkills.Execution;

internal sealed record ExecutionStepVerification(
    bool IsSuccess,
    string ReasonCode,
    VerifiedStepEvidence? Evidence)
{
    public static ExecutionStepVerification Success(VerifiedStepEvidence evidence) =>
        new(true, "execution.step_verified", evidence);

    public static ExecutionStepVerification Failure(string reasonCode) =>
        new(false, reasonCode, null);
}

internal static class ExecutionStepVerifier
{
    public static ExecutionStepVerification Verify(
        FolderSnapshot before,
        FolderSnapshot after,
        PlannedFileOperation operation)
        => VerifyCore(before, after, operation, mutationEvidence: null, requireMutationEvidence: false);

    public static ExecutionStepVerification Verify(
        FolderSnapshot before,
        FolderSnapshot after,
        PlannedFileOperation operation,
        FileMutationEvidence mutationEvidence)
    {
        ArgumentNullException.ThrowIfNull(mutationEvidence);
        return VerifyCore(
            before,
            after,
            operation,
            mutationEvidence,
            requireMutationEvidence: true);
    }

    private static ExecutionStepVerification VerifyCore(
        FolderSnapshot before,
        FolderSnapshot after,
        PlannedFileOperation operation,
        FileMutationEvidence? mutationEvidence,
        bool requireMutationEvidence)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(operation);

        if (!before.IsComplete || !after.IsComplete)
        {
            return ExecutionStepVerification.Failure("execution.verification_snapshot_incomplete");
        }

        if (before.RootIdentity != after.RootIdentity)
        {
            return ExecutionStepVerification.Failure("execution.verification_root_changed");
        }

        if (after.StartedUtc < before.CompletedUtc)
        {
            return ExecutionStepVerification.Failure("execution.verification_snapshot_overlap");
        }

        if (!TryIndex(before, out Dictionary<string, FolderSnapshotEntry>? beforeByPath) ||
            !TryIndex(after, out Dictionary<string, FolderSnapshotEntry>? afterByPath))
        {
            return ExecutionStepVerification.Failure("execution.verification_path_alias");
        }

        ExecutionStepVerification verification = operation.Primitive switch
        {
            FilePrimitive.EnsureDirectory => VerifyDirectory(
                beforeByPath!,
                afterByPath!,
                operation),
            FilePrimitive.RenameFile or FilePrimitive.MoveFile => VerifyRelocation(
                beforeByPath!,
                afterByPath!,
                operation),
            FilePrimitive.CopyFile => VerifyCopy(
                beforeByPath!,
                afterByPath!,
                operation),
            _ => ExecutionStepVerification.Failure("execution.primitive_not_allowed"),
        };
        if (!verification.IsSuccess || !requireMutationEvidence)
        {
            return verification;
        }

        VerifiedEntryEvidence destination = verification.Evidence!.Destination;
        bool mustBeCreated = operation.Primitive is
            FilePrimitive.EnsureDirectory or FilePrimitive.CopyFile;
        return string.Equals(
                destination.VolumeIdentity,
                mutationEvidence!.VolumeIdentity,
                StringComparison.Ordinal) &&
            string.Equals(
                destination.EntryIdentity,
                mutationEvidence.EntryIdentity,
                StringComparison.Ordinal) &&
            mutationEvidence.DestinationCreatedByThisCall == mustBeCreated
            ? verification
            : ExecutionStepVerification.Failure("execution.mutation_evidence_mismatch");
    }

    private static ExecutionStepVerification VerifyDirectory(
        Dictionary<string, FolderSnapshotEntry> before,
        Dictionary<string, FolderSnapshotEntry> after,
        PlannedFileOperation operation)
    {
        bool existedBefore = before.TryGetValue(
            operation.DestinationRelativePath,
            out FolderSnapshotEntry? beforeDestination);
        if (operation.DestinationPrecondition == DestinationPrecondition.Absent && existedBefore)
        {
            return ExecutionStepVerification.Failure("execution.destination_was_not_absent");
        }

        if (operation.DestinationPrecondition == DestinationPrecondition.ExistingDirectory &&
            (!existedBefore || beforeDestination!.Kind != SnapshotEntryKind.Directory))
        {
            return ExecutionStepVerification.Failure("execution.expected_directory_missing");
        }

        if (!after.TryGetValue(
                operation.DestinationRelativePath,
                out FolderSnapshotEntry? destination) ||
            destination.Kind != SnapshotEntryKind.Directory ||
            destination.IsReparsePoint)
        {
            return ExecutionStepVerification.Failure("execution.directory_postcondition_failed");
        }

        HashSet<string> excluded = operation.DestinationPrecondition == DestinationPrecondition.Absent
            ? new HashSet<string>([operation.DestinationRelativePath], StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? unexpected = FindUnexpectedChange(before, after, excluded);
        if (unexpected is not null)
        {
            return ExecutionStepVerification.Failure(unexpected);
        }

        if (operation.DestinationPrecondition == DestinationPrecondition.ExistingDirectory &&
            !EquivalentUnchanged(beforeDestination!, destination))
        {
            return ExecutionStepVerification.Failure("execution.directory_changed_unexpectedly");
        }

        return ExecutionStepVerification.Success(CreateEvidence(operation, destination));
    }

    private static ExecutionStepVerification VerifyRelocation(
        Dictionary<string, FolderSnapshotEntry> before,
        Dictionary<string, FolderSnapshotEntry> after,
        PlannedFileOperation operation)
    {
        if (!TryExpectedSource(before, operation, out FolderSnapshotEntry? source, out string? failure))
        {
            return ExecutionStepVerification.Failure(failure!);
        }

        if (before.ContainsKey(operation.DestinationRelativePath) ||
            after.ContainsKey(operation.SourceRelativePath!) ||
            !after.TryGetValue(operation.DestinationRelativePath, out FolderSnapshotEntry? destination) ||
            !DestinationMatchesSource(operation, source!, destination, requireSameIdentity: true))
        {
            return ExecutionStepVerification.Failure("execution.relocation_postcondition_failed");
        }

        HashSet<string> excluded = new(
            [operation.SourceRelativePath!, operation.DestinationRelativePath],
            StringComparer.OrdinalIgnoreCase);
        string? unexpected = FindUnexpectedChange(before, after, excluded);
        if (unexpected is not null)
        {
            return ExecutionStepVerification.Failure(unexpected);
        }

        return ExecutionStepVerification.Success(CreateEvidence(operation, destination));
    }

    private static ExecutionStepVerification VerifyCopy(
        Dictionary<string, FolderSnapshotEntry> before,
        Dictionary<string, FolderSnapshotEntry> after,
        PlannedFileOperation operation)
    {
        if (!TryExpectedSource(before, operation, out FolderSnapshotEntry? source, out string? failure))
        {
            return ExecutionStepVerification.Failure(failure!);
        }

        if (before.ContainsKey(operation.DestinationRelativePath) ||
            !after.TryGetValue(operation.SourceRelativePath!, out FolderSnapshotEntry? sourceAfter) ||
            !EquivalentUnchanged(source!, sourceAfter) ||
            !after.TryGetValue(operation.DestinationRelativePath, out FolderSnapshotEntry? destination) ||
            !DestinationMatchesSource(operation, source!, destination, requireSameIdentity: false))
        {
            return ExecutionStepVerification.Failure("execution.copy_postcondition_failed");
        }

        HashSet<string> excluded = new(
            [operation.DestinationRelativePath],
            StringComparer.OrdinalIgnoreCase);
        string? unexpected = FindUnexpectedChange(before, after, excluded);
        if (unexpected is not null)
        {
            return ExecutionStepVerification.Failure(unexpected);
        }

        return ExecutionStepVerification.Success(CreateEvidence(operation, destination));
    }

    private static bool TryExpectedSource(
        Dictionary<string, FolderSnapshotEntry> before,
        PlannedFileOperation operation,
        out FolderSnapshotEntry? source,
        out string? failureCode)
    {
        if (!before.TryGetValue(operation.SourceRelativePath!, out source) ||
            source.Kind != SnapshotEntryKind.File ||
            source.IsReparsePoint)
        {
            failureCode = "execution.source_snapshot_missing";
            return false;
        }

        SourceFileFingerprint expected = operation.SourceFingerprint!;
        if (!string.Equals(source.EntryIdentity, expected.EntryIdentity, StringComparison.Ordinal) ||
            source.Length != expected.Length ||
            source.LastWriteUtc != expected.LastWriteUtc ||
            (expected.ContentHash is not null && source.ContentHash != expected.ContentHash))
        {
            failureCode = "execution.source_snapshot_fingerprint_changed";
            return false;
        }

        failureCode = null;
        return true;
    }

    private static bool DestinationMatchesSource(
        PlannedFileOperation operation,
        FolderSnapshotEntry source,
        FolderSnapshotEntry destination,
        bool requireSameIdentity)
    {
        SourceFileFingerprint expected = operation.SourceFingerprint!;
        return destination.Kind == SnapshotEntryKind.File &&
            !destination.IsReparsePoint &&
            destination.Length == expected.Length &&
            destination.LastWriteUtc == expected.LastWriteUtc &&
            destination.Attributes == source.Attributes &&
            string.Equals(destination.VolumeIdentity, source.VolumeIdentity, StringComparison.Ordinal) &&
            (!requireSameIdentity ||
             string.Equals(destination.EntryIdentity, expected.EntryIdentity, StringComparison.Ordinal)) &&
            (expected.ContentHash is null || destination.ContentHash == expected.ContentHash) &&
            (source.ContentHashStatus != SnapshotContentHashStatus.Computed ||
             destination.ContentHash == source.ContentHash);
    }

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
            return "execution.unexpected_entry_set_changed";
        }

        foreach (FolderSnapshotEntry beforeEntry in expected)
        {
            if (!after.TryGetValue(beforeEntry.RelativePath, out FolderSnapshotEntry? afterEntry))
            {
                return "execution.unexpected_path_changed";
            }

            if (!EquivalentUnchanged(beforeEntry, afterEntry))
            {
                return beforeEntry.Kind == SnapshotEntryKind.Directory
                    ? "execution.unexpected_directory_changed"
                    : "execution.unexpected_file_changed";
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
            before.IsReparsePoint != after.IsReparsePoint ||
            before.Attributes != after.Attributes ||
            !string.Equals(before.VolumeIdentity, after.VolumeIdentity, StringComparison.Ordinal) ||
            !string.Equals(before.EntryIdentity, after.EntryIdentity, StringComparison.Ordinal))
        {
            return false;
        }

        if (before.Kind == SnapshotEntryKind.Directory)
        {
            return true;
        }

        return before.Length == after.Length &&
            before.CreationUtc == after.CreationUtc &&
            before.LastWriteUtc == after.LastWriteUtc &&
            before.ContentHashStatus == after.ContentHashStatus &&
            before.ContentHash == after.ContentHash;
    }

    private static VerifiedStepEvidence CreateEvidence(
        PlannedFileOperation operation,
        FolderSnapshotEntry destination)
    {
        VerifiedEntryEvidence entry = new(
            destination.Kind == SnapshotEntryKind.Directory
                ? VerifiedEntryKind.Directory
                : VerifiedEntryKind.File,
            destination.VolumeIdentity!,
            destination.EntryIdentity!,
            destination.Length,
            destination.CreationUtc,
            destination.LastWriteUtc,
            (int)destination.Attributes,
            destination.ContentHashStatus == SnapshotContentHashStatus.Computed
                ? destination.ContentHash
                : null);
        return new VerifiedStepEvidence(
            operation.Sequence,
            operation.Primitive,
            operation.SourceRelativePath,
            operation.DestinationRelativePath,
            operation.DestinationPrecondition == DestinationPrecondition.Absent,
            entry);
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
}
