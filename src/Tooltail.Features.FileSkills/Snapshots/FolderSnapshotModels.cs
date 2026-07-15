using System.Collections.ObjectModel;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Permissions;
using Tooltail.Features.FileSkills.Paths;

namespace Tooltail.Features.FileSkills.Snapshots;

public enum FolderSnapshotStatus
{
    Complete,
    Cancelled,
    GrantInactive,
    RootUnavailable,
    RootIdentityChanged,
    EntryLimitExceeded,
    QueueLimitExceeded,
    DurationExceeded,
    TotalHashBudgetExceeded,
    ConcurrentChange,
    PathRejected,
    InspectionFailed,
}

public enum SnapshotEntryKind
{
    File,
    Directory,
    Other,
}

public enum SnapshotContentHashStatus
{
    NotApplicable,
    Computed,
    NotPermitted,
    FileTooLarge,
}

public sealed record FolderSnapshotLimits
{
    public FolderSnapshotLimits(
        int maximumEntries,
        long maximumFileHashBytes,
        long maximumTotalHashBytes,
        int maximumQueueDepth,
        TimeSpan maximumDuration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumFileHashBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumTotalHashBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumQueueDepth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumDuration, TimeSpan.Zero);

        MaximumEntries = maximumEntries;
        MaximumFileHashBytes = maximumFileHashBytes;
        MaximumTotalHashBytes = maximumTotalHashBytes;
        MaximumQueueDepth = maximumQueueDepth;
        MaximumDuration = maximumDuration;
    }

    public int MaximumEntries { get; }

    public long MaximumFileHashBytes { get; }

    public long MaximumTotalHashBytes { get; }

    public int MaximumQueueDepth { get; }

    public TimeSpan MaximumDuration { get; }

    public static FolderSnapshotLimits Default { get; } = new(
        maximumEntries: 10_000,
        maximumFileHashBytes: 16 * 1024 * 1024,
        maximumTotalHashBytes: 128 * 1024 * 1024,
        maximumQueueDepth: 1024,
        maximumDuration: TimeSpan.FromSeconds(30));
}

public sealed record FolderSnapshotEntry
{
    public FolderSnapshotEntry(
        string relativePath,
        SnapshotEntryKind kind,
        long? length,
        DateTimeOffset creationUtc,
        DateTimeOffset lastWriteUtc,
        FileAttributes attributes,
        bool isReparsePoint,
        string? volumeIdentity,
        string? entryIdentity,
        SnapshotContentHashStatus contentHashStatus,
        ContentHash? contentHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        PathSafetyResult<WindowsRelativePath> parsed = WindowsPathPolicy.ParseRelative(relativePath);
        if (!parsed.IsSuccess ||
            !string.Equals(parsed.Value!.Value, relativePath, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Snapshot paths must be normalized Windows-relative paths.",
                nameof(relativePath));
        }

        if (length is not null)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(length.Value);
        }

        if (creationUtc.Offset != TimeSpan.Zero || lastWriteUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Snapshot timestamps must use UTC.");
        }

        if (!Enum.IsDefined(kind) || !Enum.IsDefined(contentHashStatus))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if ((kind == SnapshotEntryKind.File) != (length is not null))
        {
            throw new ArgumentException("Only file entries have a length.", nameof(length));
        }

        if ((kind == SnapshotEntryKind.File) ==
            (contentHashStatus == SnapshotContentHashStatus.NotApplicable))
        {
            throw new ArgumentException("File and non-file hash states must agree.");
        }

        if ((contentHashStatus == SnapshotContentHashStatus.Computed) != (contentHash is not null))
        {
            throw new ArgumentException("Computed hash state and content hash must agree.");
        }

        if (isReparsePoint && contentHashStatus == SnapshotContentHashStatus.Computed)
        {
            throw new ArgumentException("A reparse-point entry cannot carry a followed content hash.");
        }

        if ((volumeIdentity is null) != (entryIdentity is null) ||
            (volumeIdentity is not null && string.IsNullOrWhiteSpace(volumeIdentity)) ||
            (entryIdentity is not null && string.IsNullOrWhiteSpace(entryIdentity)))
        {
            throw new ArgumentException("Platform volume and entry identities must be present as a pair.");
        }

        RelativePath = relativePath;
        Kind = kind;
        Length = length;
        CreationUtc = creationUtc;
        LastWriteUtc = lastWriteUtc;
        Attributes = attributes;
        IsReparsePoint = isReparsePoint;
        VolumeIdentity = volumeIdentity;
        EntryIdentity = entryIdentity;
        ContentHashStatus = contentHashStatus;
        ContentHash = contentHash;
    }

    public string RelativePath { get; }

    public SnapshotEntryKind Kind { get; }

    public long? Length { get; }

    public DateTimeOffset CreationUtc { get; }

    public DateTimeOffset LastWriteUtc { get; }

    public FileAttributes Attributes { get; }

    public bool IsReparsePoint { get; }

    public string? VolumeIdentity { get; }

    public string? EntryIdentity { get; }

    public SnapshotContentHashStatus ContentHashStatus { get; }

    public ContentHash? ContentHash { get; }
}

public sealed record FolderSnapshot
{
    internal FolderSnapshot(
        ResourceRootIdentity rootIdentity,
        DateTimeOffset startedUtc,
        DateTimeOffset completedUtc,
        FolderSnapshotStatus status,
        string? reasonCode,
        long hashedBytes,
        IEnumerable<FolderSnapshotEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(rootIdentity);
        ArgumentNullException.ThrowIfNull(entries);
        if (startedUtc.Offset != TimeSpan.Zero ||
            completedUtc.Offset != TimeSpan.Zero ||
            completedUtc < startedUtc)
        {
            throw new ArgumentException("Snapshot lifecycle timestamps must be monotonic UTC values.");
        }

        if (!Enum.IsDefined(status) ||
            (status == FolderSnapshotStatus.Complete && reasonCode is not null) ||
            (status != FolderSnapshotStatus.Complete && string.IsNullOrWhiteSpace(reasonCode)))
        {
            throw new ArgumentException("Snapshot completion state and reason code must agree.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(hashedBytes);
        FolderSnapshotEntry[] materializedEntries = entries.ToArray();
        if (materializedEntries.Any(static entry => entry is null))
        {
            throw new ArgumentException("Snapshot entries cannot contain null values.", nameof(entries));
        }

        long expectedHashedBytes = 0;
        try
        {
            foreach (FolderSnapshotEntry entry in materializedEntries)
            {
                if (entry.ContentHashStatus == SnapshotContentHashStatus.Computed)
                {
                    expectedHashedBytes = checked(expectedHashedBytes + entry.Length!.Value);
                }
            }
        }
        catch (OverflowException exception)
        {
            throw new ArgumentException(
                "Snapshot hashed-byte evidence overflowed its bounded representation.",
                nameof(entries),
                exception);
        }

        if (hashedBytes != expectedHashedBytes)
        {
            throw new ArgumentException(
                "Snapshot hashed bytes must equal the retained computed-hash evidence.",
                nameof(hashedBytes));
        }

        RootIdentity = rootIdentity;
        StartedUtc = startedUtc;
        CompletedUtc = completedUtc;
        Status = status;
        ReasonCode = reasonCode;
        HashedBytes = hashedBytes;
        Entries = new ReadOnlyCollection<FolderSnapshotEntry>(materializedEntries);
    }

    public ResourceRootIdentity RootIdentity { get; }

    public DateTimeOffset StartedUtc { get; }

    public DateTimeOffset CompletedUtc { get; }

    public FolderSnapshotStatus Status { get; }

    public string? ReasonCode { get; }

    public long HashedBytes { get; }

    public IReadOnlyList<FolderSnapshotEntry> Entries { get; }

    public bool IsComplete => Status == FolderSnapshotStatus.Complete;

    public bool ContainsReparsePoints => Entries.Any(static entry => entry.IsReparsePoint);
}
