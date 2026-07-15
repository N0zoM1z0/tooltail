using Tooltail.Application.Abstractions;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Features.FileSkills.Snapshots;
using Tooltail.Platform.Windows.FileSystem;

namespace Tooltail.Platform.Windows.Tests.Snapshots;

public sealed class WindowsFolderSnapshotTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 16, 4, 0, 0, TimeSpan.Zero);

    [WindowsFact]
    [Trait("Platform", "Windows")]
    public async Task NativeProbeRootGrantAndSnapshotComposeOnTooltailOwnedFixture()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"tooltail-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "fixture.txt"), "synthetic");

        try
        {
            WindowsFileSystemPathProbe probe = new();
            PathSafetyResult<CanonicalLocalRoot> captured =
                new WindowsPathSafetyService(probe).CaptureRoot(directory);
            Assert.True(captured.IsSuccess, captured.Error?.ToString());
            LocalFolderGrant grant = LocalFolderGrant.Issue(
                new GrantId(Guid.Parse("aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa")),
                new CompanionId(Guid.Parse("bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb")),
                captured.Value!.Identity,
                [
                    GrantCapability.Enumerate,
                    GrantCapability.ReadMetadata,
                    GrantCapability.ReadContentHash,
                ],
                Now.AddMinutes(-1),
                Now.AddMinutes(10));
            FolderSnapshotService service = new(probe, new FixedClock(Now));

            FolderSnapshot snapshot = await service.CaptureAsync(captured.Value, grant);

            Assert.True(snapshot.IsComplete, snapshot.ReasonCode);
            FolderSnapshotEntry entry = Assert.Single(snapshot.Entries);
            Assert.Equal("fixture.txt", entry.RelativePath);
            Assert.Equal(SnapshotContentHashStatus.Computed, entry.ContentHashStatus);
            Assert.NotNull(entry.EntryIdentity);
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

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
