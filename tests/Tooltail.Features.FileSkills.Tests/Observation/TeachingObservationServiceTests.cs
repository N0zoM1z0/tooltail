using Tooltail.Application.Abstractions;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Features.FileSkills.Observation;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Features.FileSkills.Reconciliation;
using Tooltail.Features.FileSkills.Snapshots;
using Tooltail.Testing;

namespace Tooltail.Features.FileSkills.Tests.Observation;

public sealed class TeachingObservationServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 16, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BaselinePrecedesActivationAndHintedRenameReconcilesEndToEnd()
    {
        using TemporaryDirectory fixture = new();
        string beforePath = fixture.CreateTextFile("Inbox/report.txt", "content");
        Directory.CreateDirectory(Path.Combine(fixture.Path, "Archive"));
        FakeWatcherFactory watcherFactory = new();
        TestScope scope = CreateScope(fixture.Path, watcherFactory);

        TeachingObservationStartResult started = await scope.Service.StartAsync(
            scope.Root,
            scope.Grant);

        Assert.True(started.IsActive, started.ReasonCode);
        Assert.True(started.Baseline!.IsComplete);
        Assert.True(watcherFactory.Source.Started);
        await using TeachingObservationSession session = started.Session!;
        string afterPath = Path.Combine(fixture.Path, "Archive", "report.txt");
        File.Move(beforePath, afterPath);
        watcherFactory.Source.Emit(WatcherSourceSignal.Renamed(beforePath, afterPath));

        TeachingObservationResult stopped = await session.StopAsync(scope.Grant);

        Assert.True(stopped.WatcherHints.Quiesced);
        Assert.Equal(SnapshotReconciliationStatus.Complete, stopped.Reconciliation.Status);
        ReconciledFileEffect move = Assert.Single(
            stopped.Reconciliation.Effects,
            static effect => effect.Kind == ReconciledEffectKind.Moved);
        Assert.Equal("Inbox\\report.txt", move.SourceRelativePath);
        Assert.Equal("Archive\\report.txt", move.DestinationRelativePath);
    }

    [Fact]
    public async Task SameSnapshotsWithoutRenameHintRemainAmbiguous()
    {
        using TemporaryDirectory fixture = new();
        string beforePath = fixture.CreateTextFile("before.txt", "content");
        FakeWatcherFactory watcherFactory = new();
        TestScope scope = CreateScope(fixture.Path, watcherFactory);
        TeachingObservationStartResult started = await scope.Service.StartAsync(
            scope.Root,
            scope.Grant);
        await using TeachingObservationSession session = started.Session!;
        File.Move(beforePath, Path.Combine(fixture.Path, "after.txt"));

        TeachingObservationResult stopped = await session.StopAsync(scope.Grant);

        Assert.Equal(SnapshotReconciliationStatus.Ambiguous, stopped.Reconciliation.Status);
        Assert.False(stopped.Reconciliation.IsCompilable);
    }

    [Fact]
    public async Task InactiveGrantNeverCreatesOrStartsWatcher()
    {
        using TemporaryDirectory fixture = new();
        FakeWatcherFactory watcherFactory = new();
        TestScope scope = CreateScope(
            fixture.Path,
            watcherFactory,
            grantExpiresUtc: Now);

        TeachingObservationStartResult result = await scope.Service.StartAsync(
            scope.Root,
            scope.Grant);

        Assert.Equal(TeachingObservationStartStatus.GrantInactive, result.Status);
        Assert.Null(result.Baseline);
        Assert.Equal(0, watcherFactory.CreateCount);
    }

    [Fact]
    public async Task OverflowImmediatelyInvalidatesOtherwiseReconciledLesson()
    {
        using TemporaryDirectory fixture = new();
        string beforePath = fixture.CreateTextFile("before.txt", "content");
        FakeWatcherFactory watcherFactory = new();
        TestScope scope = CreateScope(fixture.Path, watcherFactory);
        TeachingObservationStartResult started = await scope.Service.StartAsync(
            scope.Root,
            scope.Grant);
        await using TeachingObservationSession session = started.Session!;
        string afterPath = Path.Combine(fixture.Path, "after.txt");
        File.Move(beforePath, afterPath);
        watcherFactory.Source.Emit(WatcherSourceSignal.Renamed(beforePath, afterPath));
        watcherFactory.Source.Emit(WatcherSourceSignal.Overflow());

        Assert.True(session.IsEvidenceInvalidated);
        TeachingObservationResult stopped = await session.StopAsync(scope.Grant);

        Assert.Equal(SnapshotReconciliationStatus.WatcherOverflow, stopped.Reconciliation.Status);
        Assert.False(stopped.Reconciliation.IsCompilable);
    }

    [Fact]
    public async Task BoundedHintQueueFailsClosedInsteadOfGrowingUnbounded()
    {
        using TemporaryDirectory fixture = new();
        fixture.CreateTextFile("file.txt", "content");
        FakeWatcherFactory watcherFactory = new();
        TestScope scope = CreateScope(
            fixture.Path,
            watcherFactory,
            hintLimits: new WatcherHintLimits(2, 4096, TimeSpan.FromSeconds(1)));
        TeachingObservationStartResult started = await scope.Service.StartAsync(
            scope.Root,
            scope.Grant);
        await using TeachingObservationSession session = started.Session!;
        string path = Path.Combine(fixture.Path, "file.txt");
        watcherFactory.Source.Emit(WatcherSourceSignal.Path(WatcherSourceSignalKind.Changed, path));
        watcherFactory.Source.Emit(WatcherSourceSignal.Path(WatcherSourceSignalKind.Changed, path));
        watcherFactory.Source.Emit(WatcherSourceSignal.Path(WatcherSourceSignalKind.Changed, path));

        TeachingObservationResult stopped = await session.StopAsync(scope.Grant);

        Assert.True(stopped.WatcherHints.Overflowed);
        Assert.Equal(1, stopped.WatcherHints.DroppedHintCount);
        Assert.Equal(SnapshotReconciliationStatus.WatcherOverflow, stopped.Reconciliation.Status);
    }

    [Fact]
    public async Task RevocationBeforeFinalSnapshotProducesIncompleteEvidence()
    {
        using TemporaryDirectory fixture = new();
        fixture.CreateTextFile("file.txt", "content");
        FakeWatcherFactory watcherFactory = new();
        TestScope scope = CreateScope(fixture.Path, watcherFactory);
        TeachingObservationStartResult started = await scope.Service.StartAsync(
            scope.Root,
            scope.Grant);
        await using TeachingObservationSession session = started.Session!;
        LocalFolderGrant revoked = scope.Grant.Revoke(Now, "user-revoked").Value!;

        TeachingObservationResult stopped = await session.StopAsync(revoked);

        Assert.Equal(FolderSnapshotStatus.GrantInactive, stopped.Final.Status);
        Assert.Equal(
            SnapshotReconciliationStatus.IncompleteSnapshot,
            stopped.Reconciliation.Status);
    }

    [Fact]
    public async Task DifferentGrantCannotBeSubstitutedAtStop()
    {
        using TemporaryDirectory fixture = new();
        fixture.CreateTextFile("file.txt", "content");
        FakeWatcherFactory watcherFactory = new();
        TestScope scope = CreateScope(fixture.Path, watcherFactory);
        TeachingObservationStartResult started = await scope.Service.StartAsync(
            scope.Root,
            scope.Grant);
        await using TeachingObservationSession session = started.Session!;
        LocalFolderGrant replacement = LocalFolderGrant.Issue(
            new GrantId(Guid.NewGuid()),
            scope.Grant.CompanionId,
            scope.Root.Identity,
            scope.Grant.Capabilities,
            Now.AddMinutes(-1),
            Now.AddMinutes(30));

        TeachingObservationResult stopped = await session.StopAsync(replacement);

        Assert.Equal(FolderSnapshotStatus.GrantInactive, stopped.Final.Status);
        Assert.Equal(
            SnapshotReconciliationStatus.IncompleteSnapshot,
            stopped.Reconciliation.Status);
    }

    [Fact]
    public async Task CancellationDuringStopReturnsIncompleteFinalSnapshot()
    {
        using TemporaryDirectory fixture = new();
        fixture.CreateTextFile("file.txt", "content");
        FakeWatcherFactory watcherFactory = new();
        TestScope scope = CreateScope(fixture.Path, watcherFactory);
        TeachingObservationStartResult started = await scope.Service.StartAsync(
            scope.Root,
            scope.Grant);
        await using TeachingObservationSession session = started.Session!;
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        TeachingObservationResult stopped = await session.StopAsync(
            scope.Grant,
            cancellation.Token);

        Assert.Equal(FolderSnapshotStatus.Cancelled, stopped.Final.Status);
        Assert.Equal(
            SnapshotReconciliationStatus.IncompleteSnapshot,
            stopped.Reconciliation.Status);
    }

    [Fact]
    public async Task UnquiescedCallbackStateInvalidatesFinalEvidence()
    {
        using TemporaryDirectory fixture = new();
        fixture.CreateTextFile("file.txt", "content");
        FakeWatcherFactory watcherFactory = new() { Quiesces = false };
        TestScope scope = CreateScope(fixture.Path, watcherFactory);
        TeachingObservationStartResult started = await scope.Service.StartAsync(
            scope.Root,
            scope.Grant);
        await using TeachingObservationSession session = started.Session!;

        TeachingObservationResult stopped = await session.StopAsync(scope.Grant);

        Assert.False(stopped.WatcherHints.Quiesced);
        Assert.Equal(
            SnapshotReconciliationStatus.WatcherNotQuiesced,
            stopped.Reconciliation.Status);
    }

    [Fact]
    public async Task OutOfScopeWatcherPathBecomesFaultWithoutLeakingIntoHints()
    {
        using TemporaryDirectory fixture = new();
        fixture.CreateTextFile("file.txt", "content");
        FakeWatcherFactory watcherFactory = new();
        TestScope scope = CreateScope(fixture.Path, watcherFactory);
        TeachingObservationStartResult started = await scope.Service.StartAsync(
            scope.Root,
            scope.Grant);
        await using TeachingObservationSession session = started.Session!;
        watcherFactory.Source.Emit(
            WatcherSourceSignal.Path(
                WatcherSourceSignalKind.Created,
                Path.Combine(Path.GetDirectoryName(fixture.Path)!, "outside.txt")));

        TeachingObservationResult stopped = await session.StopAsync(scope.Grant);

        Assert.True(stopped.WatcherHints.SourceFaulted);
        Assert.Empty(stopped.WatcherHints.Hints);
        Assert.Equal(SnapshotReconciliationStatus.WatcherFault, stopped.Reconciliation.Status);
    }

    [Fact]
    public async Task WatcherStartFailureReturnsVisibleInactiveResultAndDisposesSource()
    {
        using TemporaryDirectory fixture = new();
        FakeWatcherFactory watcherFactory = new() { ThrowsOnStart = true };
        TestScope scope = CreateScope(fixture.Path, watcherFactory);

        TeachingObservationStartResult result = await scope.Service.StartAsync(
            scope.Root,
            scope.Grant);

        Assert.Equal(TeachingObservationStartStatus.WatcherUnavailable, result.Status);
        Assert.NotNull(result.Baseline);
        Assert.True(watcherFactory.Source.Disposed);
    }

    private static TestScope CreateScope(
        string physicalRoot,
        FakeWatcherFactory watcherFactory,
        DateTimeOffset? grantExpiresUtc = null,
        WatcherHintLimits? hintLimits = null)
    {
        ResourceRootIdentity identity = new("observation-test-root");
        CanonicalLocalRoot root = new(
            Path.GetFullPath(physicalRoot),
            identity,
            "portable-volume",
            "portable-root");
        LocalFolderGrant grant = LocalFolderGrant.Issue(
            new GrantId(Guid.Parse("77777777-aaaa-4aaa-8aaa-777777777777")),
            new CompanionId(Guid.Parse("88888888-bbbb-4bbb-8bbb-888888888888")),
            identity,
            [
                GrantCapability.Enumerate,
                GrantCapability.ReadMetadata,
                GrantCapability.ReadContentHash,
            ],
            Now.AddMinutes(-1),
            grantExpiresUtc ?? Now.AddMinutes(30));
        FixedClock clock = new(Now);
        FolderSnapshotService snapshots = new(new PortablePathProbe(physicalRoot), clock);
        TeachingObservationService service = new(
            snapshots,
            watcherFactory,
            clock,
            hintLimits);
        return new TestScope(root, grant, service);
    }

    private sealed record TestScope(
        CanonicalLocalRoot Root,
        LocalFolderGrant Grant,
        TeachingObservationService Service);

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class PortablePathProbe : IFileSystemPathProbe
    {
        private readonly string root;

        public PortablePathProbe(string root) => this.root = Path.GetFullPath(root);

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
            bool reparse =
                (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                info.LinkTarget is not null;
            return FileSystemPathProbeResult.Found(
                fullPath,
                info is DirectoryInfo ? FileSystemEntryKind.Directory : FileSystemEntryKind.File,
                "portable-volume",
                isRoot ? "portable-root" : $"portable-entry:{fullPath}",
                reparse,
                isLocalFixedDrive: true);
        }
    }

    private sealed class FakeWatcherFactory : IWatcherHintSourceFactory
    {
        public bool Quiesces { get; init; } = true;

        public bool ThrowsOnStart { get; init; }

        public int CreateCount { get; private set; }

        public FakeWatcherSource Source { get; private set; } = null!;

        public IWatcherHintSource Create(
            string canonicalRoot,
            Action<WatcherSourceSignal> signalSink,
            int internalBufferSize)
        {
            CreateCount++;
            Source = new FakeWatcherSource(
                signalSink,
                Quiesces,
                ThrowsOnStart);
            return Source;
        }
    }

    private sealed class FakeWatcherSource(
        Action<WatcherSourceSignal> signalSink,
        bool quiesces,
        bool throwsOnStart) : IWatcherHintSource
    {
        public bool Started { get; private set; }

        public bool Disposed { get; private set; }

        public void Start()
        {
            if (throwsOnStart)
            {
                throw new IOException("Synthetic watcher failure.");
            }

            Started = true;
        }

        public void Emit(WatcherSourceSignal signal)
        {
            Assert.True(Started);
            Assert.False(Disposed);
            signalSink(signal);
        }

        public ValueTask<bool> StopAndQuiesceAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(quiesces);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
