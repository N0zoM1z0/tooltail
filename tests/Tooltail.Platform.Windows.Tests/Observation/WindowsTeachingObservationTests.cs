using System.Diagnostics;
using Tooltail.Application;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Features.FileSkills.Observation;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Features.FileSkills.Reconciliation;
using Tooltail.Features.FileSkills.Snapshots;
using Tooltail.Platform.Windows.FileSystem;

namespace Tooltail.Platform.Windows.Tests.Observation;

public sealed class WindowsTeachingObservationTests
{
    [WindowsFact]
    [Trait("Platform", "Windows")]
    public async Task NativeWatcherAndIdentitySnapshotsReconcileTooltailOwnedRename()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"tooltail-observation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string beforePath = Path.Combine(directory, "before.txt");
        string afterPath = Path.Combine(directory, "after.txt");
        await File.WriteAllTextAsync(beforePath, "synthetic");

        try
        {
            SystemClock clock = new();
            WindowsFileSystemPathProbe probe = new();
            PathSafetyResult<CanonicalLocalRoot> captured =
                new WindowsPathSafetyService(probe).CaptureRoot(directory);
            Assert.True(captured.IsSuccess, captured.Error?.ToString());
            DateTimeOffset now = clock.UtcNow;
            LocalFolderGrant grant = LocalFolderGrant.Issue(
                new GrantId(Guid.Parse("cccccccc-1111-4111-8111-cccccccccccc")),
                new CompanionId(Guid.Parse("dddddddd-2222-4222-8222-dddddddddddd")),
                captured.Value!.Identity,
                [
                    GrantCapability.Enumerate,
                    GrantCapability.ReadMetadata,
                    GrantCapability.ReadContentHash,
                ],
                now.AddMinutes(-1),
                now.AddMinutes(10));
            FolderSnapshotService snapshots = new(probe, clock);
            TeachingObservationService observations = new(
                snapshots,
                new FileSystemWatcherHintSourceFactory(),
                clock);
            TeachingObservationStartResult started = await observations.StartAsync(
                captured.Value,
                grant);
            Assert.True(started.IsActive, started.ReasonCode);
            await using TeachingObservationSession session = started.Session!;

            File.Move(beforePath, afterPath);
            Stopwatch wait = Stopwatch.StartNew();
            while (session.ObservedHintCount == 0 && wait.Elapsed < TimeSpan.FromSeconds(3))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25));
            }

            Assert.True(session.ObservedHintCount > 0, "The native watcher emitted no bounded hint.");
            TeachingObservationResult stopped = await session.StopAsync(grant);

            Assert.True(stopped.WatcherHints.Quiesced);
            Assert.False(stopped.WatcherHints.Overflowed);
            Assert.Equal(SnapshotReconciliationStatus.Complete, stopped.Reconciliation.Status);
            ReconciledFileEffect effect = Assert.Single(stopped.Reconciliation.Effects);
            Assert.Equal(ReconciledEffectKind.Renamed, effect.Kind);
            Assert.Equal("before.txt", effect.SourceRelativePath);
            Assert.Equal("after.txt", effect.DestinationRelativePath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Requires a Windows host.";
            }
        }
    }
}
