using Tooltail.Application.Abstractions;
using Tooltail.Features.FileSkills.Paths;

namespace Tooltail.Features.FileSkills.Tests.Paths;

public sealed class WindowsPathSafetyServiceTests
{
    private const string Root = "C:\\Users\\Tester\\Grant";
    private const string Volume = "volume-a";

    [Fact]
    public void CaptureRootBindsCanonicalDirectoryAndStableEntryIdentity()
    {
        SyntheticPathProbe probe = CreateRootProbe();
        WindowsPathSafetyService service = new(probe);

        PathSafetyResult<CanonicalLocalRoot> result = service.CaptureRoot(Root + "\\");

        Assert.True(result.IsSuccess);
        Assert.Equal(Root, result.Value!.CanonicalPath);
        Assert.Equal("winfs-v1:volume-a:grant-id", result.Value.Identity.Value);
    }

    [Fact]
    public void CaptureRootRejectsNonFixedAndReparseRoots()
    {
        SyntheticPathProbe nonFixed = CreateRootProbe();
        nonFixed.SetFound(Root, FileSystemEntryKind.Directory, "grant-id", isLocalFixedDrive: false);
        SyntheticPathProbe reparse = CreateRootProbe();
        reparse.SetFound(Root, FileSystemEntryKind.Directory, "grant-id", isReparsePoint: true);

        PathSafetyResult<CanonicalLocalRoot> nonFixedResult =
            new WindowsPathSafetyService(nonFixed).CaptureRoot(Root);
        PathSafetyResult<CanonicalLocalRoot> reparseResult =
            new WindowsPathSafetyService(reparse).CaptureRoot(Root);

        Assert.Equal(PathSafetyReasonCodes.RootNotFixedDrive, nonFixedResult.Error?.Code);
        Assert.Equal(PathSafetyReasonCodes.ReparsePoint, reparseResult.Error?.Code);
    }

    [Theory]
    [InlineData("\\\\server\\share", PathSafetyReasonCodes.Unc)]
    [InlineData("\\\\?\\C:\\Grant", PathSafetyReasonCodes.Device)]
    [InlineData("C:\\Grant:stream", PathSafetyReasonCodes.AlternateStream)]
    public void CaptureRootRejectsNonLocalAndAlternateStreamSyntax(
        string path,
        string expectedCode)
    {
        PathSafetyResult<CanonicalLocalRoot> result =
            new WindowsPathSafetyService(CreateRootProbe()).CaptureRoot(path);

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedCode, result.Error?.Code);
    }

    [Fact]
    public void BindingWalksEveryExistingComponentWithoutFollowingLinks()
    {
        SyntheticPathProbe probe = CreateRootProbe();
        probe.SetFound($"{Root}\\Inbox", FileSystemEntryKind.Directory, "inbox-id");
        probe.SetFound($"{Root}\\Inbox\\file.txt", FileSystemEntryKind.File, "file-id");
        WindowsPathSafetyService service = new(probe);
        CanonicalLocalRoot root = service.CaptureRoot(Root).Value!;

        PathSafetyResult<BoundLocalPath> result = service.Bind(
            root,
            "Inbox\\file.txt",
            PathEntryExpectation.MustExist);

        Assert.True(result.IsSuccess);
        Assert.Collection(
            result.Value!.Components,
            component => Assert.Equal("inbox-id", component.EntryIdentity),
            component => Assert.Equal("file-id", component.EntryIdentity));
    }

    [Fact]
    public void RevalidationRejectsLinkIntroducedAfterPlanning()
    {
        SyntheticPathProbe probe = CreateRootProbe();
        probe.SetFound($"{Root}\\Inbox", FileSystemEntryKind.Directory, "inbox-id");
        probe.SetFound($"{Root}\\Inbox\\file.txt", FileSystemEntryKind.File, "file-id");
        WindowsPathSafetyService service = new(probe);
        CanonicalLocalRoot root = service.CaptureRoot(Root).Value!;
        BoundLocalPath binding = service.Bind(
            root,
            "Inbox\\file.txt",
            PathEntryExpectation.MustExist).Value!;

        probe.SetFound(
            $"{Root}\\Inbox",
            FileSystemEntryKind.Directory,
            "replacement-link-id",
            isReparsePoint: true);
        PathSafetyResult<BoundLocalPath> result = service.Revalidate(binding);

        Assert.False(result.IsSuccess);
        Assert.Equal(PathSafetyReasonCodes.ReparsePoint, result.Error?.Code);
    }

    [Fact]
    public void RevalidationRejectsChangedSourceIdentity()
    {
        SyntheticPathProbe probe = CreateRootProbe();
        probe.SetFound($"{Root}\\file.txt", FileSystemEntryKind.File, "source-v1");
        WindowsPathSafetyService service = new(probe);
        CanonicalLocalRoot root = service.CaptureRoot(Root).Value!;
        BoundLocalPath binding = service.Bind(
            root,
            "file.txt",
            PathEntryExpectation.MustExist).Value!;

        probe.SetFound($"{Root}\\file.txt", FileSystemEntryKind.File, "source-v2");
        PathSafetyResult<BoundLocalPath> result = service.Revalidate(binding);

        Assert.False(result.IsSuccess);
        Assert.Equal(PathSafetyReasonCodes.IdentityChanged, result.Error?.Code);
    }

    [Fact]
    public void RevalidationRejectsDestinationThatAppearsAfterPlanning()
    {
        SyntheticPathProbe probe = CreateRootProbe();
        probe.SetFound($"{Root}\\Output", FileSystemEntryKind.Directory, "output-id");
        WindowsPathSafetyService service = new(probe);
        CanonicalLocalRoot root = service.CaptureRoot(Root).Value!;
        BoundLocalPath binding = service.Bind(
            root,
            "Output\\new.txt",
            PathEntryExpectation.MustNotExist).Value!;

        probe.SetFound($"{Root}\\Output\\new.txt", FileSystemEntryKind.File, "unexpected-id");
        PathSafetyResult<BoundLocalPath> result = service.Revalidate(binding);

        Assert.False(result.IsSuccess);
        Assert.Equal(PathSafetyReasonCodes.DestinationExists, result.Error?.Code);
    }

    [Fact]
    public void RevalidationRejectsRootReplacement()
    {
        SyntheticPathProbe probe = CreateRootProbe();
        WindowsPathSafetyService service = new(probe);
        CanonicalLocalRoot root = service.CaptureRoot(Root).Value!;

        probe.SetFound(Root, FileSystemEntryKind.Directory, "replacement-root-id");
        PathSafetyResult<BoundLocalPath> result = service.Bind(
            root,
            "file.txt",
            PathEntryExpectation.MustNotExist);

        Assert.False(result.IsSuccess);
        Assert.Equal(PathSafetyReasonCodes.RootIdentityChanged, result.Error?.Code);
    }

    private static SyntheticPathProbe CreateRootProbe()
    {
        SyntheticPathProbe probe = new();
        probe.SetFound("C:\\", FileSystemEntryKind.Directory, "drive-root-id");
        probe.SetFound("C:\\Users", FileSystemEntryKind.Directory, "users-id");
        probe.SetFound("C:\\Users\\Tester", FileSystemEntryKind.Directory, "tester-id");
        probe.SetFound(Root, FileSystemEntryKind.Directory, "grant-id");
        return probe;
    }

    private sealed class SyntheticPathProbe : IFileSystemPathProbe
    {
        private readonly Dictionary<string, FileSystemPathProbeResult> results =
            new(StringComparer.OrdinalIgnoreCase);

        public FileSystemPathProbeResult Probe(string absolutePath) =>
            results.TryGetValue(Trim(absolutePath), out FileSystemPathProbeResult? result)
                ? result
                : FileSystemPathProbeResult.Failed(
                    FileSystemPathProbeStatus.NotFound,
                    "synthetic.not_found");

        public void SetFound(
            string path,
            FileSystemEntryKind kind,
            string entryIdentity,
            bool isReparsePoint = false,
            bool isLocalFixedDrive = true)
        {
            string canonicalPath = Trim(path);
            results[canonicalPath] = FileSystemPathProbeResult.Found(
                canonicalPath,
                kind,
                Volume,
                entryIdentity,
                isReparsePoint,
                isLocalFixedDrive);
        }

        private static string Trim(string path) =>
            path.Length > 3 ? path.TrimEnd('\\') : path;
    }
}
