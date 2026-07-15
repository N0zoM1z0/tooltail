using System.Buffers;
using System.Security.Cryptography;
using Tooltail.Application.Abstractions;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Permissions;
using Tooltail.Features.FileSkills.Paths;

namespace Tooltail.Features.FileSkills.Snapshots;

public sealed class FolderSnapshotService
{
    private const int HashBufferSize = 64 * 1024;
    private const FileAttributes RetainedAttributes =
        FileAttributes.ReadOnly |
        FileAttributes.Hidden |
        FileAttributes.System |
        FileAttributes.Archive |
        FileAttributes.Temporary |
        FileAttributes.Offline |
        FileAttributes.Encrypted |
        FileAttributes.Compressed |
        FileAttributes.SparseFile |
        FileAttributes.ReparsePoint;

    private readonly IFileSystemPathProbe pathProbe;
    private readonly IClock clock;
    private readonly FolderSnapshotLimits limits;

    public FolderSnapshotService(
        IFileSystemPathProbe pathProbe,
        IClock clock,
        FolderSnapshotLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(pathProbe);
        ArgumentNullException.ThrowIfNull(clock);
        this.pathProbe = pathProbe;
        this.clock = clock;
        this.limits = limits ?? FolderSnapshotLimits.Default;
    }

    public async Task<FolderSnapshot> CaptureAsync(
        CanonicalLocalRoot root,
        LocalFolderGrant grant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(grant);
        DateTimeOffset startedUtc = clock.UtcNow;
        List<FolderSnapshotEntry> entries = [];
        long hashedBytes = 0;

        if (startedUtc.Offset != TimeSpan.Zero ||
            grant.RootIdentity != root.Identity ||
            !grant.Allows(GrantCapability.Enumerate, startedUtc) ||
            !grant.Allows(GrantCapability.ReadMetadata, startedUtc))
        {
            return Incomplete(
                root,
                startedUtc,
                FolderSnapshotStatus.GrantInactive,
                "snapshot.grant_inactive",
                hashedBytes,
                entries);
        }

        FolderSnapshotStatus? rootStatus = ValidateRoot(root);
        if (rootStatus is not null)
        {
            return Incomplete(
                root,
                startedUtc,
                rootStatus.Value,
                RootReasonCode(rootStatus.Value),
                hashedBytes,
                entries);
        }

        try
        {
            Queue<PendingDirectory> pending = new();
            pending.Enqueue(
                new PendingDirectory(
                    new DirectoryInfo(root.CanonicalPath),
                    root.EntryIdentity));
            while (pending.Count > 0)
            {
                FolderSnapshot? limitFailure = CheckRunState(
                    root,
                    grant,
                    startedUtc,
                    hashedBytes,
                    entries,
                    cancellationToken);
                if (limitFailure is not null)
                {
                    return limitFailure;
                }

                PendingDirectory pendingDirectory = pending.Dequeue();
                FolderSnapshotStatus? directoryStatus = ValidatePendingDirectory(
                    root,
                    pendingDirectory);
                if (directoryStatus is not null)
                {
                    return Incomplete(
                        root,
                        startedUtc,
                        directoryStatus.Value,
                        DirectoryReasonCode(directoryStatus.Value),
                        hashedBytes,
                        entries);
                }

                DirectoryInfo directory = pendingDirectory.Directory;
                List<FileSystemInfo> children = [];
                foreach (FileSystemInfo child in directory.EnumerateFileSystemInfos(
                             "*",
                             new EnumerationOptions
                             {
                                 AttributesToSkip = 0,
                                 IgnoreInaccessible = false,
                                 RecurseSubdirectories = false,
                                 ReturnSpecialDirectories = false,
                             }))
                {
                    if (entries.Count + children.Count >= limits.MaximumEntries)
                    {
                        return Incomplete(
                            root,
                            startedUtc,
                            FolderSnapshotStatus.EntryLimitExceeded,
                            "snapshot.entry_limit_exceeded",
                            hashedBytes,
                            entries);
                    }

                    children.Add(child);
                }

                children.Sort(FileSystemInfoComparer.Instance);
                foreach (FileSystemInfo child in children)
                {
                    FolderSnapshot? entryLimitFailure = CheckRunState(
                        root,
                        grant,
                        startedUtc,
                        hashedBytes,
                        entries,
                        cancellationToken);
                    if (entryLimitFailure is not null)
                    {
                        return entryLimitFailure;
                    }

                    SnapshotEntryCapture capture = await CaptureEntryAsync(
                        root,
                        grant,
                        child,
                        startedUtc,
                        hashedBytes,
                        cancellationToken).ConfigureAwait(false);
                    if (capture.Status is not null)
                    {
                        return Incomplete(
                            root,
                            startedUtc,
                            capture.Status.Value,
                            capture.ReasonCode!,
                            hashedBytes,
                            entries);
                    }

                    entries.Add(capture.Entry!);
                    hashedBytes += capture.HashedBytes;
                    if (capture.EnqueueDirectory)
                    {
                        FolderSnapshotEntry directoryEntry = capture.Entry!;
                        pending.Enqueue(
                            new PendingDirectory(
                                (DirectoryInfo)child,
                                directoryEntry.EntryIdentity!));
                        if (pending.Count > limits.MaximumQueueDepth)
                        {
                            return Incomplete(
                                root,
                                startedUtc,
                                FolderSnapshotStatus.QueueLimitExceeded,
                                "snapshot.queue_limit_exceeded",
                                hashedBytes,
                                entries);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            return Incomplete(
                root,
                startedUtc,
                FolderSnapshotStatus.Cancelled,
                "snapshot.cancelled",
                hashedBytes,
                entries);
        }
        catch (UnauthorizedAccessException)
        {
            return Incomplete(
                root,
                startedUtc,
                FolderSnapshotStatus.InspectionFailed,
                "snapshot.access_denied",
                hashedBytes,
                entries);
        }
        catch (IOException)
        {
            return Incomplete(
                root,
                startedUtc,
                FolderSnapshotStatus.ConcurrentChange,
                "snapshot.io_or_concurrent_change",
                hashedBytes,
                entries);
        }

        FolderSnapshotStatus? finalRootStatus = ValidateRoot(root);
        if (finalRootStatus is not null)
        {
            return Incomplete(
                root,
                startedUtc,
                finalRootStatus.Value,
                RootReasonCode(finalRootStatus.Value),
                hashedBytes,
                entries);
        }

        FolderSnapshot? finalLimitFailure = CheckRunState(
            root,
            grant,
            startedUtc,
            hashedBytes,
            entries,
            cancellationToken);
        if (finalLimitFailure is not null)
        {
            return finalLimitFailure;
        }

        entries.Sort(SnapshotEntryComparer.Instance);
        for (int index = 1; index < entries.Count; index++)
        {
            if (string.Equals(
                    entries[index - 1].RelativePath,
                    entries[index].RelativePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Incomplete(
                    root,
                    startedUtc,
                    FolderSnapshotStatus.PathRejected,
                    "snapshot.case_conflicting_paths",
                    hashedBytes,
                    entries);
            }
        }

        DateTimeOffset completedUtc = clock.UtcNow;
        FolderSnapshot? completionFailure = CheckRunStateAt(
            root,
            grant,
            startedUtc,
            completedUtc,
            hashedBytes,
            entries,
            cancellationToken);
        if (completionFailure is not null)
        {
            return completionFailure;
        }

        return new FolderSnapshot(
            root.Identity,
            startedUtc,
            completedUtc,
            FolderSnapshotStatus.Complete,
            reasonCode: null,
            hashedBytes,
            entries);
    }

    private async Task<SnapshotEntryCapture> CaptureEntryAsync(
        CanonicalLocalRoot root,
        LocalFolderGrant grant,
        FileSystemInfo fileSystemInfo,
        DateTimeOffset startedUtc,
        long hashedBytesSoFar,
        CancellationToken cancellationToken)
    {
        fileSystemInfo.Refresh();
        FileAttributes attributes = fileSystemInfo.Attributes;
        bool isReparsePoint =
            (attributes & FileAttributes.ReparsePoint) != 0 ||
            fileSystemInfo.LinkTarget is not null;
        string operatingSystemRelative = Path.GetRelativePath(root.CanonicalPath, fileSystemInfo.FullName);
        if (Path.DirectorySeparatorChar != '\\' && operatingSystemRelative.Contains('\\'))
        {
            return SnapshotEntryCapture.Failed(
                FolderSnapshotStatus.PathRejected,
                "path.invalid_separator");
        }

        string relative = operatingSystemRelative
            .Replace(Path.DirectorySeparatorChar, '\\')
            .Replace(Path.AltDirectorySeparatorChar, '\\');
        PathSafetyResult<WindowsRelativePath> parsed = WindowsPathPolicy.ParseRelative(relative);
        if (!parsed.IsSuccess)
        {
            return SnapshotEntryCapture.Failed(
                FolderSnapshotStatus.PathRejected,
                parsed.Error!.Code);
        }

        FileSystemPathProbeResult probe = pathProbe.Probe(fileSystemInfo.FullName);
        if (probe.Status == FileSystemPathProbeStatus.NotFound)
        {
            return SnapshotEntryCapture.Failed(
                FolderSnapshotStatus.ConcurrentChange,
                "snapshot.entry_disappeared");
        }

        if (probe.Status != FileSystemPathProbeStatus.Success)
        {
            return SnapshotEntryCapture.Failed(
                FolderSnapshotStatus.InspectionFailed,
                "snapshot.entry_inspection_failed");
        }

        if (!string.Equals(probe.VolumeIdentity, root.VolumeIdentity, StringComparison.Ordinal))
        {
            return SnapshotEntryCapture.Failed(
                FolderSnapshotStatus.RootIdentityChanged,
                "snapshot.volume_changed");
        }

        if (!IsWithinPhysicalRoot(root.CanonicalPath, probe.CanonicalPath!))
        {
            return SnapshotEntryCapture.Failed(
                FolderSnapshotStatus.RootIdentityChanged,
                "snapshot.entry_resolved_outside_root");
        }

        isReparsePoint |= probe.IsReparsePoint;
        SnapshotEntryKind enumeratedKind = fileSystemInfo switch
        {
            DirectoryInfo => SnapshotEntryKind.Directory,
            FileInfo => SnapshotEntryKind.File,
            _ => SnapshotEntryKind.Other,
        };
        SnapshotEntryKind probedKind = probe.EntryKind switch
        {
            FileSystemEntryKind.Directory => SnapshotEntryKind.Directory,
            FileSystemEntryKind.File => SnapshotEntryKind.File,
            _ => SnapshotEntryKind.Other,
        };
        if (enumeratedKind != probedKind)
        {
            return SnapshotEntryCapture.Failed(
                FolderSnapshotStatus.ConcurrentChange,
                "snapshot.entry_kind_changed");
        }

        SnapshotEntryKind kind = probedKind;
        long? length = fileSystemInfo is FileInfo file ? file.Length : null;
        DateTime creationTimeUtc = fileSystemInfo.CreationTimeUtc;
        DateTime lastWriteTimeUtc = fileSystemInfo.LastWriteTimeUtc;
        FileAttributes retainedAttributes = attributes & RetainedAttributes;
        SnapshotContentHashStatus hashStatus = kind == SnapshotEntryKind.File
            ? SnapshotContentHashStatus.NotPermitted
            : SnapshotContentHashStatus.NotApplicable;
        ContentHash? contentHash = null;
        long newlyHashedBytes = 0;

        if (kind == SnapshotEntryKind.File &&
            !isReparsePoint &&
            grant.Allows(GrantCapability.ReadContentHash, clock.UtcNow))
        {
            if (length > limits.MaximumFileHashBytes)
            {
                hashStatus = SnapshotContentHashStatus.FileTooLarge;
            }
            else
            {
                HashCapture hash = await HashFileAsync(
                    (FileInfo)fileSystemInfo,
                    probe,
                    length!.Value,
                    creationTimeUtc,
                    lastWriteTimeUtc,
                    retainedAttributes,
                    grant,
                    startedUtc,
                    hashedBytesSoFar,
                    cancellationToken).ConfigureAwait(false);
                if (hash.Status is not null)
                {
                    return SnapshotEntryCapture.Failed(hash.Status.Value, hash.ReasonCode!);
                }

                hashStatus = SnapshotContentHashStatus.Computed;
                contentHash = hash.ContentHash;
                newlyHashedBytes = hash.HashedBytes;
            }
        }

        FolderSnapshotEntry entry = new(
            parsed.Value!.Value,
            kind,
            length,
            AsUtc(creationTimeUtc),
            AsUtc(lastWriteTimeUtc),
            retainedAttributes,
            isReparsePoint,
            probe.VolumeIdentity,
            probe.EntryIdentity,
            hashStatus,
            contentHash);
        return SnapshotEntryCapture.Succeeded(
            entry,
            newlyHashedBytes,
            enqueueDirectory: kind == SnapshotEntryKind.Directory && !isReparsePoint);
    }

    private async Task<HashCapture> HashFileAsync(
        FileInfo file,
        FileSystemPathProbeResult expectedProbe,
        long expectedLength,
        DateTime expectedCreationTimeUtc,
        DateTime expectedLastWriteTimeUtc,
        FileAttributes expectedAttributes,
        LocalFolderGrant grant,
        DateTimeOffset startedUtc,
        long hashedBytesSoFar,
        CancellationToken cancellationToken)
    {
        if (hashedBytesSoFar > limits.MaximumTotalHashBytes ||
            expectedLength > limits.MaximumTotalHashBytes - hashedBytesSoFar)
        {
            return HashCapture.Failed(
                FolderSnapshotStatus.TotalHashBudgetExceeded,
                "snapshot.total_hash_budget_exceeded");
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(HashBufferSize);
        long fileHashedBytes = 0;
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using FileStream stream = new(
                file.FullName,
                new FileStreamOptions
                {
                    Access = FileAccess.Read,
                    BufferSize = HashBufferSize,
                    Mode = FileMode.Open,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                    Share = FileShare.Read,
                });
            if (!HashProbeStillMatches(file.FullName, expectedProbe))
            {
                return HashCapture.Failed(
                    FolderSnapshotStatus.ConcurrentChange,
                    "snapshot.file_identity_changed_before_hash");
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DateTimeOffset now = clock.UtcNow;
                if (now.Offset != TimeSpan.Zero ||
                    now < startedUtc ||
                    HasDurationExpired(now, startedUtc))
                {
                    return HashCapture.Failed(
                        FolderSnapshotStatus.DurationExceeded,
                        "snapshot.duration_exceeded");
                }

                if (!grant.Allows(GrantCapability.ReadContentHash, now))
                {
                    return HashCapture.Failed(
                        FolderSnapshotStatus.GrantInactive,
                        "snapshot.grant_inactive");
                }

                int read = await stream
                    .ReadAsync(buffer.AsMemory(0, HashBufferSize), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                fileHashedBytes += read;
                if (fileHashedBytes > limits.MaximumFileHashBytes)
                {
                    return HashCapture.Failed(
                        FolderSnapshotStatus.ConcurrentChange,
                        "snapshot.file_grew_during_hash");
                }

                if (fileHashedBytes > limits.MaximumTotalHashBytes - hashedBytesSoFar)
                {
                    return HashCapture.Failed(
                        FolderSnapshotStatus.TotalHashBudgetExceeded,
                        "snapshot.total_hash_budget_exceeded");
                }

                hash.AppendData(buffer, 0, read);
            }

            file.Refresh();
            if (!file.Exists ||
                file.Length != expectedLength ||
                file.CreationTimeUtc != expectedCreationTimeUtc ||
                file.LastWriteTimeUtc != expectedLastWriteTimeUtc ||
                (file.Attributes & RetainedAttributes) != expectedAttributes ||
                !HashProbeStillMatches(file.FullName, expectedProbe))
            {
                return HashCapture.Failed(
                    FolderSnapshotStatus.ConcurrentChange,
                    "snapshot.file_changed_during_hash");
            }

            return HashCapture.Succeeded(
                new ContentHash(Convert.ToHexStringLower(hash.GetHashAndReset())),
                fileHashedBytes);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private FolderSnapshotStatus? ValidatePendingDirectory(
        CanonicalLocalRoot root,
        PendingDirectory pendingDirectory)
    {
        if (PathsEqual(pendingDirectory.Directory.FullName, root.CanonicalPath))
        {
            return ValidateRoot(root);
        }

        FileSystemPathProbeResult probe = pathProbe.Probe(pendingDirectory.Directory.FullName);
        if (probe.Status == FileSystemPathProbeStatus.NotFound)
        {
            return FolderSnapshotStatus.ConcurrentChange;
        }

        if (probe.Status != FileSystemPathProbeStatus.Success)
        {
            return FolderSnapshotStatus.InspectionFailed;
        }

        if (probe.EntryKind != FileSystemEntryKind.Directory ||
            probe.IsReparsePoint ||
            !probe.IsLocalFixedDrive ||
            !string.Equals(probe.VolumeIdentity, root.VolumeIdentity, StringComparison.Ordinal) ||
            !string.Equals(
                probe.EntryIdentity,
                pendingDirectory.ExpectedEntryIdentity,
                StringComparison.Ordinal) ||
            !IsWithinPhysicalRoot(root.CanonicalPath, probe.CanonicalPath!))
        {
            return FolderSnapshotStatus.ConcurrentChange;
        }

        return null;
    }

    private bool HashProbeStillMatches(
        string path,
        FileSystemPathProbeResult expectedProbe)
    {
        FileSystemPathProbeResult current = pathProbe.Probe(path);
        return current.Status == FileSystemPathProbeStatus.Success &&
            current.EntryKind == FileSystemEntryKind.File &&
            !current.IsReparsePoint &&
            current.IsLocalFixedDrive &&
            PathsEqual(current.CanonicalPath!, expectedProbe.CanonicalPath!) &&
            string.Equals(
                current.VolumeIdentity,
                expectedProbe.VolumeIdentity,
                StringComparison.Ordinal) &&
            string.Equals(
                current.EntryIdentity,
                expectedProbe.EntryIdentity,
                StringComparison.Ordinal);
    }

    private FolderSnapshot? CheckRunState(
        CanonicalLocalRoot root,
        LocalFolderGrant grant,
        DateTimeOffset startedUtc,
        long hashedBytes,
        IReadOnlyCollection<FolderSnapshotEntry> entries,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.UtcNow;
        return CheckRunStateAt(
            root,
            grant,
            startedUtc,
            now,
            hashedBytes,
            entries,
            cancellationToken);
    }

    private FolderSnapshot? CheckRunStateAt(
        CanonicalLocalRoot root,
        LocalFolderGrant grant,
        DateTimeOffset startedUtc,
        DateTimeOffset now,
        long hashedBytes,
        IReadOnlyCollection<FolderSnapshotEntry> entries,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Incomplete(
                root,
                startedUtc,
                FolderSnapshotStatus.Cancelled,
                "snapshot.cancelled",
                hashedBytes,
                entries);
        }

        if (now.Offset != TimeSpan.Zero || now < startedUtc || HasDurationExpired(now, startedUtc))
        {
            return Incomplete(
                root,
                startedUtc,
                FolderSnapshotStatus.DurationExceeded,
                "snapshot.duration_exceeded",
                hashedBytes,
                entries);
        }

        if (!grant.Allows(GrantCapability.Enumerate, now) ||
            !grant.Allows(GrantCapability.ReadMetadata, now))
        {
            return Incomplete(
                root,
                startedUtc,
                FolderSnapshotStatus.GrantInactive,
                "snapshot.grant_inactive",
                hashedBytes,
                entries);
        }

        return null;
    }

    private FolderSnapshotStatus? ValidateRoot(CanonicalLocalRoot root)
    {
        FileSystemPathProbeResult probe = pathProbe.Probe(root.CanonicalPath);
        if (probe.Status == FileSystemPathProbeStatus.NotFound)
        {
            return FolderSnapshotStatus.RootUnavailable;
        }

        if (probe.Status != FileSystemPathProbeStatus.Success)
        {
            return FolderSnapshotStatus.InspectionFailed;
        }

        if (probe.EntryKind != FileSystemEntryKind.Directory ||
            probe.IsReparsePoint ||
            !probe.IsLocalFixedDrive ||
            !PathsEqual(probe.CanonicalPath!, root.CanonicalPath) ||
            !string.Equals(probe.VolumeIdentity, root.VolumeIdentity, StringComparison.Ordinal) ||
            !string.Equals(probe.EntryIdentity, root.EntryIdentity, StringComparison.Ordinal))
        {
            return FolderSnapshotStatus.RootIdentityChanged;
        }

        return null;
    }

    private bool HasDurationExpired(DateTimeOffset now, DateTimeOffset startedUtc) =>
        now - startedUtc > limits.MaximumDuration;

    private FolderSnapshot Incomplete(
        CanonicalLocalRoot root,
        DateTimeOffset startedUtc,
        FolderSnapshotStatus status,
        string reasonCode,
        long hashedBytes,
        IEnumerable<FolderSnapshotEntry> entries) =>
        new(
            root.Identity,
            startedUtc,
            SafeCompletionTime(startedUtc),
            status,
            reasonCode,
            hashedBytes,
            entries);

    private DateTimeOffset SafeCompletionTime(DateTimeOffset startedUtc)
    {
        DateTimeOffset now = clock.UtcNow;
        return now.Offset == TimeSpan.Zero && now >= startedUtc ? now : startedUtc;
    }

    private static string RootReasonCode(FolderSnapshotStatus status) =>
        status switch
        {
            FolderSnapshotStatus.RootUnavailable => "snapshot.root_unavailable",
            FolderSnapshotStatus.RootIdentityChanged => "snapshot.root_identity_changed",
            _ => "snapshot.root_inspection_failed",
        };

    private static string DirectoryReasonCode(FolderSnapshotStatus status) =>
        status switch
        {
            FolderSnapshotStatus.RootUnavailable or
            FolderSnapshotStatus.RootIdentityChanged => RootReasonCode(status),
            FolderSnapshotStatus.InspectionFailed => "snapshot.directory_inspection_failed",
            _ => "snapshot.directory_changed_before_enumeration",
        };

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static bool IsWithinPhysicalRoot(string root, string candidate)
    {
        string relative = Path.GetRelativePath(root, candidate);
        return relative != ".." &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private sealed record SnapshotEntryCapture(
        FolderSnapshotEntry? Entry,
        long HashedBytes,
        bool EnqueueDirectory,
        FolderSnapshotStatus? Status,
        string? ReasonCode)
    {
        public static SnapshotEntryCapture Succeeded(
            FolderSnapshotEntry entry,
            long hashedBytes,
            bool enqueueDirectory) =>
            new(entry, hashedBytes, enqueueDirectory, null, null);

        public static SnapshotEntryCapture Failed(
            FolderSnapshotStatus status,
            string reasonCode) =>
            new(null, 0, false, status, reasonCode);
    }

    private sealed record HashCapture(
        ContentHash? ContentHash,
        long HashedBytes,
        FolderSnapshotStatus? Status,
        string? ReasonCode)
    {
        public static HashCapture Succeeded(ContentHash hash, long hashedBytes) =>
            new(hash, hashedBytes, null, null);

        public static HashCapture Failed(FolderSnapshotStatus status, string reasonCode) =>
            new(null, 0, status, reasonCode);
    }

    private sealed record PendingDirectory(
        DirectoryInfo Directory,
        string ExpectedEntryIdentity);

    private sealed class FileSystemInfoComparer : IComparer<FileSystemInfo>
    {
        public static FileSystemInfoComparer Instance { get; } = new();

        public int Compare(FileSystemInfo? left, FileSystemInfo? right)
        {
            int insensitive = StringComparer.OrdinalIgnoreCase.Compare(left?.Name, right?.Name);
            return insensitive != 0
                ? insensitive
                : StringComparer.Ordinal.Compare(left?.Name, right?.Name);
        }
    }

    private sealed class SnapshotEntryComparer : IComparer<FolderSnapshotEntry>
    {
        public static SnapshotEntryComparer Instance { get; } = new();

        public int Compare(FolderSnapshotEntry? left, FolderSnapshotEntry? right)
        {
            int insensitive = StringComparer.OrdinalIgnoreCase.Compare(
                left?.RelativePath,
                right?.RelativePath);
            return insensitive != 0
                ? insensitive
                : StringComparer.Ordinal.Compare(left?.RelativePath, right?.RelativePath);
        }
    }
}
