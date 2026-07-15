using System.Security.Cryptography;
using Tooltail.Application.Abstractions;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Features.FileSkills.Snapshots;
using Tooltail.Testing;

namespace Tooltail.Features.FileSkills.Tests.Snapshots;

public sealed class FolderSnapshotServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 16, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CompleteSnapshotIsSortedAndHashesBoundedFiles()
    {
        using TemporaryDirectory fixture = new();
        Directory.CreateDirectory(Path.Combine(fixture.Path, "Empty"));
        fixture.CreateTextFile("Inbox/report.txt", "hello");
        TestScope scope = CreateScope(fixture.Path);

        FolderSnapshot snapshot = await scope.Service.CaptureAsync(scope.Root, scope.Grant);

        Assert.True(snapshot.IsComplete, snapshot.ReasonCode);
        Assert.Equal(5, snapshot.HashedBytes);
        Assert.Collection(
            snapshot.Entries,
            entry =>
            {
                Assert.Equal("Empty", entry.RelativePath);
                Assert.Equal(SnapshotEntryKind.Directory, entry.Kind);
            },
            entry =>
            {
                Assert.Equal("Inbox", entry.RelativePath);
                Assert.Equal(SnapshotEntryKind.Directory, entry.Kind);
            },
            entry =>
            {
                Assert.Equal("Inbox\\report.txt", entry.RelativePath);
                Assert.Equal(SnapshotEntryKind.File, entry.Kind);
                Assert.Equal(SnapshotContentHashStatus.Computed, entry.ContentHashStatus);
                Assert.Equal(
                    Convert.ToHexStringLower(SHA256.HashData("hello"u8.ToArray())),
                    entry.ContentHash!.Value);
            });
    }

    [Fact]
    public async Task SameTreeProducesSameEntryProjection()
    {
        using TemporaryDirectory fixture = new();
        fixture.CreateTextFile("b.txt", "b");
        fixture.CreateTextFile("A/a.txt", "a");
        TestScope scope = CreateScope(fixture.Path);

        FolderSnapshot first = await scope.Service.CaptureAsync(scope.Root, scope.Grant);
        FolderSnapshot second = await scope.Service.CaptureAsync(scope.Root, scope.Grant);

        Assert.True(first.IsComplete);
        Assert.True(second.IsComplete);
        Assert.Equal(first.Entries, second.Entries);
    }

    [Fact]
    public async Task MissingHashCapabilityProducesExplicitUnhashedState()
    {
        using TemporaryDirectory fixture = new();
        fixture.CreateTextFile("file.txt", "content");
        TestScope scope = CreateScope(
            fixture.Path,
            capabilities: [GrantCapability.Enumerate, GrantCapability.ReadMetadata]);

        FolderSnapshot snapshot = await scope.Service.CaptureAsync(scope.Root, scope.Grant);

        Assert.True(snapshot.IsComplete);
        FolderSnapshotEntry file = Assert.Single(snapshot.Entries);
        Assert.Equal(SnapshotContentHashStatus.NotPermitted, file.ContentHashStatus);
        Assert.Null(file.ContentHash);
        Assert.Equal(0, snapshot.HashedBytes);
    }

    [Fact]
    public async Task PerFileHashLimitSkipsLargeFileWithoutCreatingPartialHash()
    {
        using TemporaryDirectory fixture = new();
        fixture.CreateTextFile("large.txt", "123456");
        TestScope scope = CreateScope(
            fixture.Path,
            limits: Limits(maximumFileHashBytes: 5));

        FolderSnapshot snapshot = await scope.Service.CaptureAsync(scope.Root, scope.Grant);

        Assert.True(snapshot.IsComplete);
        Assert.Equal(SnapshotContentHashStatus.FileTooLarge, Assert.Single(snapshot.Entries).ContentHashStatus);
        Assert.Equal(0, snapshot.HashedBytes);
    }

    [Fact]
    public async Task TotalHashBudgetExhaustionIsNeverAValidPartialSnapshot()
    {
        using TemporaryDirectory fixture = new();
        fixture.CreateTextFile("file.txt", "123456");
        TestScope scope = CreateScope(
            fixture.Path,
            limits: Limits(maximumFileHashBytes: 10, maximumTotalHashBytes: 5));

        FolderSnapshot snapshot = await scope.Service.CaptureAsync(scope.Root, scope.Grant);

        Assert.False(snapshot.IsComplete);
        Assert.Equal(FolderSnapshotStatus.TotalHashBudgetExceeded, snapshot.Status);
        Assert.Equal("snapshot.total_hash_budget_exceeded", snapshot.ReasonCode);
        Assert.Empty(snapshot.Entries);
    }

    [Fact]
    public async Task FileIdentityChangeBeforeFirstReadInvalidatesHashEvidence()
    {
        using TemporaryDirectory fixture = new();
        string file = fixture.CreateTextFile("file.txt", "content");
        IFileSystemPathProbe probe = new ChangingEntryIdentityProbe(
            new PortablePathProbe(fixture.Path),
            file);
        TestScope scope = CreateScope(fixture.Path, probe: probe);

        FolderSnapshot snapshot = await scope.Service.CaptureAsync(scope.Root, scope.Grant);

        Assert.Equal(FolderSnapshotStatus.ConcurrentChange, snapshot.Status);
        Assert.Equal("snapshot.file_identity_changed_before_hash", snapshot.ReasonCode);
        Assert.Empty(snapshot.Entries);
        Assert.Equal(0, snapshot.HashedBytes);
    }

    [Fact]
    public async Task QueuedDirectoryIdentityChangeStopsBeforeChildEnumeration()
    {
        using TemporaryDirectory fixture = new();
        string directory = Directory.CreateDirectory(Path.Combine(fixture.Path, "Inbox")).FullName;
        fixture.CreateTextFile("Inbox/file.txt", "content");
        IFileSystemPathProbe probe = new ChangingEntryIdentityProbe(
            new PortablePathProbe(fixture.Path),
            directory);
        TestScope scope = CreateScope(fixture.Path, probe: probe);

        FolderSnapshot snapshot = await scope.Service.CaptureAsync(scope.Root, scope.Grant);

        Assert.Equal(FolderSnapshotStatus.ConcurrentChange, snapshot.Status);
        Assert.Equal("snapshot.directory_changed_before_enumeration", snapshot.ReasonCode);
        FolderSnapshotEntry observedDirectory = Assert.Single(snapshot.Entries);
        Assert.Equal("Inbox", observedDirectory.RelativePath);
    }

    [Fact]
    public async Task EntryAndQueueLimitsFailClosed()
    {
        using TemporaryDirectory entryFixture = new();
        entryFixture.CreateTextFile("one.txt", "1");
        entryFixture.CreateTextFile("two.txt", "2");
        TestScope entryScope = CreateScope(
            entryFixture.Path,
            limits: Limits(maximumEntries: 1));

        FolderSnapshot entryLimited = await entryScope.Service.CaptureAsync(
            entryScope.Root,
            entryScope.Grant);

        using TemporaryDirectory queueFixture = new();
        Directory.CreateDirectory(Path.Combine(queueFixture.Path, "one"));
        Directory.CreateDirectory(Path.Combine(queueFixture.Path, "two"));
        TestScope queueScope = CreateScope(
            queueFixture.Path,
            limits: Limits(maximumQueueDepth: 1));

        FolderSnapshot queueLimited = await queueScope.Service.CaptureAsync(
            queueScope.Root,
            queueScope.Grant);

        Assert.Equal(FolderSnapshotStatus.EntryLimitExceeded, entryLimited.Status);
        Assert.Equal(FolderSnapshotStatus.QueueLimitExceeded, queueLimited.Status);
        Assert.False(entryLimited.IsComplete);
        Assert.False(queueLimited.IsComplete);
    }

    [Fact]
    public async Task PreCancelledCaptureReturnsIncompleteSnapshotWithoutThrowing()
    {
        using TemporaryDirectory fixture = new();
        fixture.CreateTextFile("file.txt", "content");
        TestScope scope = CreateScope(fixture.Path);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        FolderSnapshot snapshot = await scope.Service.CaptureAsync(
            scope.Root,
            scope.Grant,
            cancellation.Token);

        Assert.Equal(FolderSnapshotStatus.Cancelled, snapshot.Status);
        Assert.False(snapshot.IsComplete);
    }

    [Fact]
    public async Task ExpiredGrantCannotReadEvenAnEmptyRoot()
    {
        using TemporaryDirectory fixture = new();
        TestScope scope = CreateScope(fixture.Path, grantExpiresUtc: Now);

        FolderSnapshot snapshot = await scope.Service.CaptureAsync(scope.Root, scope.Grant);

        Assert.Equal(FolderSnapshotStatus.GrantInactive, snapshot.Status);
        Assert.False(snapshot.IsComplete);
    }

    [NonWindowsFact]
    public async Task ReparseDirectoryIsRecordedButNeverTraversed()
    {
        using TemporaryDirectory fixture = new();
        string target = Path.Combine(fixture.Path, "Target");
        Directory.CreateDirectory(target);
        fixture.CreateTextFile("Target/secret.txt", "synthetic");
        Directory.CreateSymbolicLink(Path.Combine(fixture.Path, "Link"), target);
        TestScope scope = CreateScope(fixture.Path);

        FolderSnapshot snapshot = await scope.Service.CaptureAsync(scope.Root, scope.Grant);

        Assert.True(snapshot.IsComplete, snapshot.ReasonCode);
        Assert.True(snapshot.ContainsReparsePoints);
        Assert.Contains(snapshot.Entries, entry =>
            entry.RelativePath == "Link" && entry.IsReparsePoint);
        Assert.DoesNotContain(snapshot.Entries, entry =>
            entry.RelativePath.StartsWith("Link\\", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RootReplacementDetectedAtFinalRevalidationInvalidatesSnapshot()
    {
        using TemporaryDirectory fixture = new();
        fixture.CreateTextFile("file.txt", "content");
        PortablePathProbe probe = new(fixture.Path) { StableRootProbeCount = 2 };
        TestScope scope = CreateScope(fixture.Path, probe: probe);

        FolderSnapshot snapshot = await scope.Service.CaptureAsync(scope.Root, scope.Grant);

        Assert.Equal(FolderSnapshotStatus.RootIdentityChanged, snapshot.Status);
        Assert.False(snapshot.IsComplete);
    }

    [Fact]
    public async Task DurationBudgetProducesIncompleteSnapshot()
    {
        using TemporaryDirectory fixture = new();
        fixture.CreateTextFile("file.txt", "content");
        AdvancingClock clock = new(Now, TimeSpan.FromMilliseconds(2));
        TestScope scope = CreateScope(
            fixture.Path,
            limits: Limits(maximumDuration: TimeSpan.FromMilliseconds(1)),
            clock: clock);

        FolderSnapshot snapshot = await scope.Service.CaptureAsync(scope.Root, scope.Grant);

        Assert.Equal(FolderSnapshotStatus.DurationExceeded, snapshot.Status);
        Assert.False(snapshot.IsComplete);
    }

    [Fact]
    public void SnapshotEntryRejectsUnnormalizedPathsAndImpossibleFileShapes()
    {
        Assert.Throws<ArgumentException>(
            () => new FolderSnapshotEntry(
                "folder/../file.txt",
                SnapshotEntryKind.File,
                1,
                Now,
                Now,
                FileAttributes.Normal,
                isReparsePoint: false,
                "volume",
                "entry",
                SnapshotContentHashStatus.NotPermitted,
                contentHash: null));

        Assert.Throws<ArgumentException>(
            () => new FolderSnapshotEntry(
                "file.txt",
                SnapshotEntryKind.File,
                length: null,
                Now,
                Now,
                FileAttributes.Normal,
                isReparsePoint: false,
                "volume",
                "entry",
                SnapshotContentHashStatus.NotPermitted,
                contentHash: null));
    }

    [Fact]
    public void SnapshotRejectsHashedByteClaimsWithoutRetainedHashEvidence()
    {
        FolderSnapshotEntry entry = new(
            "file.txt",
            SnapshotEntryKind.File,
            1,
            Now,
            Now,
            FileAttributes.Normal,
            isReparsePoint: false,
            "volume",
            "entry",
            SnapshotContentHashStatus.NotPermitted,
            contentHash: null);

        Assert.Throws<ArgumentException>(
            () => new FolderSnapshot(
                new ResourceRootIdentity("snapshot-root"),
                Now,
                Now,
                FolderSnapshotStatus.Complete,
                reasonCode: null,
                hashedBytes: 1,
                [entry]));
    }

    private static TestScope CreateScope(
        string physicalRoot,
        IEnumerable<GrantCapability>? capabilities = null,
        FolderSnapshotLimits? limits = null,
        DateTimeOffset? grantExpiresUtc = null,
        IFileSystemPathProbe? probe = null,
        IClock? clock = null)
    {
        const string volumeIdentity = "portable-volume";
        const string rootEntryIdentity = "portable-root";
        ResourceRootIdentity identity = new("snapshot-test-root");
        CanonicalLocalRoot root = new(
            Path.GetFullPath(physicalRoot),
            identity,
            volumeIdentity,
            rootEntryIdentity);
        LocalFolderGrant grant = LocalFolderGrant.Issue(
            new GrantId(Guid.Parse("11111111-aaaa-4aaa-8aaa-111111111111")),
            new CompanionId(Guid.Parse("22222222-bbbb-4bbb-8bbb-222222222222")),
            identity,
            capabilities ??
            [
                GrantCapability.Enumerate,
                GrantCapability.ReadMetadata,
                GrantCapability.ReadContentHash,
            ],
            Now.AddMinutes(-1),
            grantExpiresUtc ?? Now.AddMinutes(30));
        IFileSystemPathProbe actualProbe = probe ?? new PortablePathProbe(physicalRoot);
        IClock actualClock = clock ?? new FixedClock(Now);
        return new TestScope(
            root,
            grant,
            new FolderSnapshotService(actualProbe, actualClock, limits));
    }

    private static FolderSnapshotLimits Limits(
        int maximumEntries = 100,
        long maximumFileHashBytes = 1024,
        long maximumTotalHashBytes = 4096,
        int maximumQueueDepth = 100,
        TimeSpan? maximumDuration = null) =>
        new(
            maximumEntries,
            maximumFileHashBytes,
            maximumTotalHashBytes,
            maximumQueueDepth,
            maximumDuration ?? TimeSpan.FromMinutes(1));

    private sealed record TestScope(
        CanonicalLocalRoot Root,
        LocalFolderGrant Grant,
        FolderSnapshotService Service);

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class AdvancingClock : IClock
    {
        private readonly TimeSpan increment;
        private DateTimeOffset current;

        public AdvancingClock(DateTimeOffset initial, TimeSpan increment)
        {
            current = initial - increment;
            this.increment = increment;
        }

        public DateTimeOffset UtcNow
        {
            get
            {
                current += increment;
                return current;
            }
        }
    }

    private sealed class PortablePathProbe : IFileSystemPathProbe
    {
        private readonly string root;
        private int rootProbeCount;

        public PortablePathProbe(string root) => this.root = Path.GetFullPath(root);

        public int StableRootProbeCount { get; init; } = int.MaxValue;

        public FileSystemPathProbeResult Probe(string absolutePath)
        {
            string fullPath = Path.GetFullPath(absolutePath);
            FileSystemInfo? info = Directory.Exists(fullPath)
                ? new DirectoryInfo(fullPath)
                : File.Exists(fullPath)
                    ? new FileInfo(fullPath)
                    : null;
            if (info is null)
            {
                return FileSystemPathProbeResult.Failed(
                    FileSystemPathProbeStatus.NotFound,
                    "portable.not_found");
            }

            info.Refresh();
            bool isRoot = string.Equals(fullPath, root, StringComparison.Ordinal);
            if (isRoot)
            {
                rootProbeCount++;
            }

            string entryIdentity = isRoot
                ? rootProbeCount > StableRootProbeCount
                    ? "replacement-root"
                    : "portable-root"
                : $"portable-entry:{fullPath}";
            bool reparse =
                (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                info.LinkTarget is not null;
            return FileSystemPathProbeResult.Found(
                fullPath,
                info is DirectoryInfo ? FileSystemEntryKind.Directory : FileSystemEntryKind.File,
                "portable-volume",
                entryIdentity,
                reparse,
                isLocalFixedDrive: true);
        }
    }

    private sealed class ChangingEntryIdentityProbe(
        IFileSystemPathProbe inner,
        string changingPath) : IFileSystemPathProbe
    {
        private readonly string changingFullPath = Path.GetFullPath(changingPath);
        private int matchingProbeCount;

        public FileSystemPathProbeResult Probe(string absolutePath)
        {
            FileSystemPathProbeResult result = inner.Probe(absolutePath);
            if (result.Status != FileSystemPathProbeStatus.Success ||
                !string.Equals(
                    Path.GetFullPath(absolutePath),
                    changingFullPath,
                    StringComparison.Ordinal))
            {
                return result;
            }

            matchingProbeCount++;
            return FileSystemPathProbeResult.Found(
                result.CanonicalPath!,
                result.EntryKind!.Value,
                result.VolumeIdentity!,
                matchingProbeCount == 1
                    ? result.EntryIdentity!
                    : $"replacement:{result.EntryIdentity}",
                result.IsReparsePoint,
                result.IsLocalFixedDrive);
        }
    }

    private sealed class NonWindowsFactAttribute : FactAttribute
    {
        public NonWindowsFactAttribute()
        {
            if (OperatingSystem.IsWindows())
            {
                Skip = "Portable symlink fixture runs on non-Windows hosts; native Windows link coverage is separately tagged.";
            }
        }
    }
}
