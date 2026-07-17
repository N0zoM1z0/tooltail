using System.Security.Cryptography;
using Tooltail.Application.Abstractions;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Platform.Windows.FileSystem;

namespace Tooltail.Platform.Windows.Tests.FileSystem;

public sealed class WindowsHandleBoundFileMutationEngineTests
{
    [WindowsFact]
    [Trait("Platform", "Windows")]
    public void EnsureDirectoryLosesExactCreateRaceWithoutOwnershipEvidence()
    {
        using OwnedDirectory fixture = new();
        FileMutationRootBinding root = CaptureRoot(fixture.Path);
        string destination = Path.Combine(fixture.Path, "Review");
        bool competitorCreated = false;
        WindowsHandleBoundFileMutationEngine engine = new(
            new ActionBoundaryHook(
                _ =>
                {
                    Directory.CreateDirectory(destination);
                    competitorCreated = true;
                }));

        FileMutationPreparationResult preparation = engine.Prepare(
            FileMutationRequest.CreateDirectory(root, "Review"));
        Assert.True(preparation.IsSuccess);
        using IPreparedFileMutation mutation = preparation.PreparedMutation!;
        FileMutationResult result = mutation.Execute();

        Assert.True(competitorCreated);
        Assert.False(result.IsSuccess);
        Assert.Equal(FileMutationFailureKind.DestinationExists, result.FailureKind);
        Assert.Null(result.Evidence);
        Assert.True(Directory.Exists(destination));
    }

    [WindowsFact]
    [Trait("Platform", "Windows")]
    public void DestinationParentSwapAfterHandlesLockIsBlockedAndCopyStaysInsideRoot()
    {
        using OwnedDirectory fixture = new();
        string inbox = Directory.CreateDirectory(Path.Combine(fixture.Path, "Inbox")).FullName;
        string review = Directory.CreateDirectory(Path.Combine(fixture.Path, "Review")).FullName;
        string displaced = Path.Combine(fixture.Path, "Review-displaced");
        string source = Path.Combine(inbox, "invoice.pdf");
        File.WriteAllText(source, "handle-bound-copy");
        File.SetLastWriteTimeUtc(
            source,
            new DateTime(2026, 7, 17, 1, 2, 3, DateTimeKind.Utc));
        FileMutationRootBinding root = CaptureRoot(fixture.Path);
        FileMutationExpectedEntry expected = CaptureFile(root, source);
        bool swapBlocked = false;
        WindowsHandleBoundFileMutationEngine engine = new(
            new ActionBoundaryHook(
                _ =>
                {
                    try
                    {
                        Directory.Move(review, displaced);
                    }
                    catch (IOException)
                    {
                        swapBlocked = true;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        swapBlocked = true;
                    }
                }));

        FileMutationPreparationResult preparation = engine.Prepare(
            FileMutationRequest.CopyFile(
                root,
                "Inbox\\invoice.pdf",
                "Review\\invoice.pdf",
                expected,
                maximumCopyBytes: 1024));
        Assert.True(preparation.IsSuccess);
        FileMutationResult result;
        using (IPreparedFileMutation mutation = preparation.PreparedMutation!)
        {
            result = mutation.Execute();
        }

        FileMutationEvidence? evidence = result.Evidence;
        bool succeeded = result.IsSuccess;

        Assert.True(swapBlocked);
        Assert.True(succeeded);
        Assert.NotNull(evidence);
        Assert.True(evidence.DestinationCreatedByThisCall);
        string copied = Path.Combine(review, "invoice.pdf");
        Assert.Equal("handle-bound-copy", File.ReadAllText(copied));
        Assert.False(Directory.Exists(displaced));
        FileSystemPathProbeResult copiedProbe = new WindowsFileSystemPathProbe().Probe(copied);
        Assert.Equal(evidence.EntryIdentity, copiedProbe.EntryIdentity);
    }

    [WindowsFact]
    [Trait("Platform", "Windows")]
    public void LateChildAfterDirectoryIdentityLockIsBlockedOrPreventsUndoRemoval()
    {
        using OwnedDirectory fixture = new();
        string created = Directory.CreateDirectory(Path.Combine(fixture.Path, "Review")).FullName;
        FileMutationRootBinding root = CaptureRoot(fixture.Path);
        FileSystemPathProbeResult createdProbe = new WindowsFileSystemPathProbe().Probe(created);
        FileMutationExpectedEntry expected = new(
            FileSystemEntryKind.Directory,
            root.VolumeIdentity,
            createdProbe.EntryIdentity!,
            attributes: 0);
        string lateChild = Path.Combine(created, "late.txt");
        bool childInserted = false;
        WindowsHandleBoundFileMutationEngine engine = new(
            new ActionBoundaryHook(
                _ =>
                {
                    try
                    {
                        File.WriteAllText(lateChild, "concurrent user work");
                        childInserted = true;
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }));

        FileMutationPreparationResult preparation = engine.Prepare(
            FileMutationRequest.RemoveCreatedEntry(root, "Review", expected));
        Assert.True(preparation.IsSuccess);
        using IPreparedFileMutation mutation = preparation.PreparedMutation!;
        FileMutationResult result = mutation.Execute();

        if (childInserted)
        {
            Assert.False(result.IsSuccess);
            Assert.Equal(FileMutationFailureKind.DirectoryNotEmpty, result.FailureKind);
            Assert.True(Directory.Exists(created));
            Assert.Equal("concurrent user work", File.ReadAllText(lateChild));
        }
        else
        {
            Assert.True(result.IsSuccess);
            Assert.False(Directory.Exists(created));
        }
    }

    private static FileMutationRootBinding CaptureRoot(string path)
    {
        PathSafetyResult<CanonicalLocalRoot> captured =
            new WindowsPathSafetyService(new WindowsFileSystemPathProbe()).CaptureRoot(path);
        Assert.True(captured.IsSuccess, captured.Error?.ToString());
        CanonicalLocalRoot root = captured.Value!;
        return new FileMutationRootBinding(
            root.CanonicalPath,
            root.VolumeIdentity,
            root.EntryIdentity);
    }

    private static FileMutationExpectedEntry CaptureFile(
        FileMutationRootBinding root,
        string path)
    {
        FileSystemPathProbeResult probe = new WindowsFileSystemPathProbe().Probe(path);
        Assert.Equal(FileSystemPathProbeStatus.Success, probe.Status);
        FileInfo file = new(path);
        return new FileMutationExpectedEntry(
            FileSystemEntryKind.File,
            root.VolumeIdentity,
            probe.EntryIdentity!,
            file.Length,
            lastWriteUtc: new DateTimeOffset(
                DateTime.SpecifyKind(file.LastWriteTimeUtc, DateTimeKind.Utc)),
            contentHash: Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
    }

    private sealed class ActionBoundaryHook(Action<FileMutationRequest> action)
        : IWindowsFileMutationBoundaryHook
    {
        public void AfterHandlesLockedBeforeEffect(FileMutationRequest request) =>
            action(request);
    }

    private sealed class OwnedDirectory : IDisposable
    {
        public OwnedDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"tooltail-native-mutation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute(
            [System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
            [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
            : base(sourceFilePath, (int)sourceLineNumber)
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Requires a Windows host.";
            }
        }
    }
}
