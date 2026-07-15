using Tooltail.Application.Abstractions;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Features.FileSkills.Snapshots;

namespace Tooltail.Features.FileSkills.Rehearsal;

public sealed record RehearsalFixtureLimits
{
    public RehearsalFixtureLimits(int maximumEntries, long maximumTotalFileBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumTotalFileBytes, 1);
        MaximumEntries = maximumEntries;
        MaximumTotalFileBytes = maximumTotalFileBytes;
    }

    public int MaximumEntries { get; }

    public long MaximumTotalFileBytes { get; }

    public static RehearsalFixtureLimits Default { get; } = new(
        maximumEntries: 2_000,
        maximumTotalFileBytes: 64 * 1024 * 1024);
}

internal sealed record RehearsalStagingResult(bool IsSuccess, string ReasonCode)
{
    public static RehearsalStagingResult Success { get; } =
        new(true, "rehearsal.fixture_staged");

    public static RehearsalStagingResult Failure(string reasonCode) =>
        new(false, reasonCode);
}

internal sealed class RehearsalFixtureStager
{
    private readonly WindowsPathSafetyService pathSafety;
    private readonly RehearsalFixtureLimits limits;

    public RehearsalFixtureStager(
        WindowsPathSafetyService pathSafety,
        RehearsalFixtureLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(pathSafety);
        this.pathSafety = pathSafety;
        this.limits = limits ?? RehearsalFixtureLimits.Default;
    }

    public ValueTask<RehearsalStagingResult> StageAsync(
        CanonicalLocalRoot sourceRoot,
        FolderSnapshot sourceSnapshot,
        RehearsalWorkspace workspace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceRoot);
        ArgumentNullException.ThrowIfNull(sourceSnapshot);
        ArgumentNullException.ThrowIfNull(workspace);
        string? snapshotFailure = ValidateSnapshot(sourceRoot, sourceSnapshot);
        if (snapshotFailure is not null)
        {
            return ValueTask.FromResult(RehearsalStagingResult.Failure(snapshotFailure));
        }

        try
        {
            foreach (FolderSnapshotEntry directory in sourceSnapshot.Entries
                         .Where(static entry => entry.Kind == SnapshotEntryKind.Directory)
                         .OrderBy(static entry => PathDepth(entry.RelativePath))
                         .ThenBy(static entry => entry.RelativePath, PathComparer.Instance))
            {
                cancellationToken.ThrowIfCancellationRequested();
                PathSafetyResult<BoundLocalPath> destination = pathSafety.Bind(
                    workspace.Root,
                    directory.RelativePath,
                    PathEntryExpectation.MustNotExist);
                if (!destination.IsSuccess || !ParentsExist(destination.Value))
                {
                    return ValueTask.FromResult(
                        RehearsalStagingResult.Failure(
                            destination.Error?.Code ?? "rehearsal.fixture_parent_missing"));
                }

                Directory.CreateDirectory(destination.Value!.FullPath);
            }

            foreach (FolderSnapshotEntry file in sourceSnapshot.Entries
                         .Where(static entry => entry.Kind == SnapshotEntryKind.File)
                         .OrderBy(static entry => entry.RelativePath, PathComparer.Instance))
            {
                cancellationToken.ThrowIfCancellationRequested();
                PathSafetyResult<BoundLocalPath> source = pathSafety.Bind(
                    sourceRoot,
                    file.RelativePath,
                    PathEntryExpectation.MustExist);
                if (!source.IsSuccess ||
                    source.Value!.Components[^1].EntryKind != FileSystemEntryKind.File ||
                    !string.Equals(
                        source.Value.Components[^1].EntryIdentity,
                        file.EntryIdentity,
                        StringComparison.Ordinal))
                {
                    return ValueTask.FromResult(
                        RehearsalStagingResult.Failure(
                            source.Error?.Code ?? "rehearsal.source_identity_changed"));
                }

                FileInfo current = new(source.Value.FullPath);
                current.Refresh();
                if (!current.Exists ||
                    current.Length != file.Length ||
                    AsUtc(current.LastWriteTimeUtc) != file.LastWriteUtc)
                {
                    return ValueTask.FromResult(
                        RehearsalStagingResult.Failure("rehearsal.source_fingerprint_changed"));
                }

                PathSafetyResult<BoundLocalPath> destination = pathSafety.Bind(
                    workspace.Root,
                    file.RelativePath,
                    PathEntryExpectation.MustNotExist);
                if (!destination.IsSuccess || !ParentsExist(destination.Value))
                {
                    return ValueTask.FromResult(
                        RehearsalStagingResult.Failure(
                            destination.Error?.Code ?? "rehearsal.fixture_parent_missing"));
                }

                File.Copy(source.Value.FullPath, destination.Value!.FullPath, overwrite: false);
                if (file.Attributes != 0)
                {
                    File.SetAttributes(destination.Value.FullPath, file.Attributes);
                }

                File.SetCreationTimeUtc(destination.Value.FullPath, file.CreationUtc.UtcDateTime);
                File.SetLastWriteTimeUtc(destination.Value.FullPath, file.LastWriteUtc.UtcDateTime);
                PathSafetyResult<BoundLocalPath> unchangedSource = pathSafety.Revalidate(source.Value);
                if (!unchangedSource.IsSuccess)
                {
                    return ValueTask.FromResult(
                        RehearsalStagingResult.Failure(unchangedSource.Error!.Code));
                }
            }

            return ValueTask.FromResult(RehearsalStagingResult.Success);
        }
        catch (OperationCanceledException)
        {
            return ValueTask.FromResult(
                RehearsalStagingResult.Failure("rehearsal.cancelled"));
        }
        catch (UnauthorizedAccessException)
        {
            return ValueTask.FromResult(
                RehearsalStagingResult.Failure("rehearsal.fixture_access_denied"));
        }
        catch (IOException)
        {
            return ValueTask.FromResult(
                RehearsalStagingResult.Failure("rehearsal.fixture_io_failure"));
        }
    }

    private string? ValidateSnapshot(
        CanonicalLocalRoot sourceRoot,
        FolderSnapshot snapshot)
    {
        if (!snapshot.IsComplete || snapshot.RootIdentity != sourceRoot.Identity)
        {
            return "rehearsal.source_snapshot_invalid";
        }

        if (snapshot.Entries.Count > limits.MaximumEntries)
        {
            return "rehearsal.fixture_entry_limit_exceeded";
        }

        long totalBytes = 0;
        try
        {
            foreach (FolderSnapshotEntry entry in snapshot.Entries)
            {
                if (entry.IsReparsePoint ||
                    entry.Kind == SnapshotEntryKind.Other ||
                    entry.VolumeIdentity is null ||
                    entry.EntryIdentity is null)
                {
                    return "rehearsal.fixture_unsupported_entry";
                }

                if (entry.Kind == SnapshotEntryKind.File)
                {
                    if (entry.ContentHashStatus != SnapshotContentHashStatus.Computed)
                    {
                        return "rehearsal.fixture_hash_required";
                    }

                    totalBytes = checked(totalBytes + entry.Length!.Value);
                    if (totalBytes > limits.MaximumTotalFileBytes)
                    {
                        return "rehearsal.fixture_byte_limit_exceeded";
                    }
                }
            }
        }
        catch (OverflowException)
        {
            return "rehearsal.fixture_byte_limit_exceeded";
        }

        return null;
    }

    private static bool ParentsExist(BoundLocalPath? path) =>
        path is not null &&
        path.Components
            .Take(path.Components.Count - 1)
            .All(static component =>
                component.Existed &&
                component.EntryKind == FileSystemEntryKind.Directory);

    private static int PathDepth(string relativePath) =>
        relativePath.Count(static value => value == '\\');

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed class PathComparer : IComparer<string>
    {
        public static PathComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            int insensitive = StringComparer.OrdinalIgnoreCase.Compare(left, right);
            return insensitive != 0
                ? insensitive
                : StringComparer.Ordinal.Compare(left, right);
        }
    }
}
