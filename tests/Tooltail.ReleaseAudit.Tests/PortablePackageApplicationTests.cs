using System.IO.Compression;
using System.Text.Json;
using Tooltail.ReleaseAudit;
using Tooltail.Testing;

namespace Tooltail.ReleaseAudit.Tests;

public sealed class PortablePackageApplicationTests
{
    [Fact]
    public void SamePayloadProducesByteIdenticalBoundedArchivesAndSidecars()
    {
        using TemporaryDirectory temporary = new();
        string publish = CreateSyntheticPublish(temporary.Path);
        string first = Path.Combine(temporary.Path, "first.zip");
        string second = Path.Combine(temporary.Path, "second.zip");

        PortablePackageVerification firstResult =
            PortablePackageApplication.BuildArchive(publish, first);
        PortablePackageVerification secondResult =
            PortablePackageApplication.BuildArchive(publish, second);

        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        Assert.Equal(firstResult.ArchiveSha256, secondResult.ArchiveSha256);
        Assert.Equal(RequiredPayload.Length, firstResult.FileCount);
        Assert.True(firstResult.Manifest.SelfContained);
        Assert.False(firstResult.Manifest.IsCodeSigned);
        Assert.True(firstResult.Manifest.UninstallPreservesData);
        Assert.Equal("program_directory_only", firstResult.Manifest.UninstallScope);
        Assert.Equal("%LOCALAPPDATA%\\Tooltail", firstResult.Manifest.DataRoot);
        Assert.Contains(
            $"{firstResult.ArchiveSha256}  first.zip",
            File.ReadAllText($"{first}.sha256"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void UnmanifestedEntryAndProhibitedPublishStateFailClosed()
    {
        using TemporaryDirectory temporary = new();
        string publish = CreateSyntheticPublish(temporary.Path);
        string prohibited = Path.Combine(publish, "tooltail.db");
        File.WriteAllText(prohibited, "must not package");

        Assert.Throws<InvalidDataException>(() =>
            PortablePackageApplication.BuildArchive(
                publish,
                Path.Combine(temporary.Path, "prohibited.zip")));

        File.Delete(prohibited);
        string archive = Path.Combine(temporary.Path, "valid.zip");
        _ = PortablePackageApplication.BuildArchive(publish, archive);
        using (ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            ZipArchiveEntry extra = zip.CreateEntry("Tooltail/unmanifested.txt");
            using StreamWriter writer = new(extra.Open());
            writer.Write("unexpected");
        }

        Assert.Throws<InvalidDataException>(() =>
            PortablePackageApplication.VerifyArchive(archive));
    }

    [Fact]
    public async Task SuccessfulFixtureRemovalDeletesOnlyProgramAndRetainsLocalData()
    {
        using TemporaryDirectory temporary = new();
        string publish = CreateSyntheticPublish(temporary.Path);
        string archive = Path.Combine(temporary.Path, "portable.zip");
        _ = PortablePackageApplication.BuildArchive(publish, archive);
        string work = Path.Combine(temporary.Path, "verification");
        string? launched = null;

        PortableUninstallVerification result =
            await PortablePackageApplication.VerifyUninstallFixtureAsync(
                archive,
                work,
                (executable, _) =>
                {
                    launched = executable;
                    return Task.FromResult(0);
                },
                TestContext.Current.CancellationToken);

        Assert.EndsWith("Tooltail.Desktop.exe", launched, StringComparison.Ordinal);
        Assert.True(result.ProgramDirectoryRemoved);
        Assert.True(result.LocalDataSentinelPreserved);
        Assert.False(Directory.Exists(Path.Combine(work, "program")));
        Assert.True(File.Exists(Path.Combine(
            work,
            "local-data",
            "retained-user-data.sentinel")));
        using JsonDocument evidence = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(work, "uninstall-evidence.json")));
        Assert.True(evidence.RootElement
            .GetProperty("localDataSentinelPreserved").GetBoolean());
    }

    [Fact]
    public async Task FailedApphostLeavesProgramAndDataForInspection()
    {
        using TemporaryDirectory temporary = new();
        string publish = CreateSyntheticPublish(temporary.Path);
        string archive = Path.Combine(temporary.Path, "portable.zip");
        _ = PortablePackageApplication.BuildArchive(publish, archive);
        string work = Path.Combine(temporary.Path, "failed-verification");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PortablePackageApplication.VerifyUninstallFixtureAsync(
                archive,
                work,
                (_, _) => Task.FromResult(7),
                TestContext.Current.CancellationToken));

        Assert.True(Directory.Exists(Path.Combine(work, "program")));
        Assert.True(File.Exists(Path.Combine(
            work,
            "local-data",
            "retained-user-data.sentinel")));
        Assert.False(File.Exists(Path.Combine(work, "uninstall-evidence.json")));
    }

    [Fact]
    public async Task CliRejectsArbitraryPackageOutputWithoutCreatingIt()
    {
        string root = RepositoryRoot();
        string prohibited = Path.Combine(root, "prohibited-portable.zip");
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await PortablePackageApplication.RunAsync(
            [
                "pack-portable",
                "--root",
                root,
                "--publish",
                Path.Combine(root, "artifacts", "portable", "win-x64", "publish"),
                "--output",
                prohibited,
            ],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(prohibited));
        Assert.Contains("portable_package.failed", error.ToString(), StringComparison.Ordinal);
    }

    private static string CreateSyntheticPublish(string root)
    {
        string publish = Path.Combine(root, "publish");
        Directory.CreateDirectory(publish);
        for (int index = 0; index < RequiredPayload.Length; index++)
        {
            File.WriteAllText(
                Path.Combine(publish, RequiredPayload[index]),
                $"synthetic portable payload {index}\n");
        }

        return publish;
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? current = new(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "Tooltail.sln")))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate Tooltail.sln.");
    }

    private static readonly string[] RequiredPayload =
    [
        "Tooltail.Desktop.exe",
        "Tooltail.Desktop.dll",
        "Tooltail.Desktop.deps.json",
        "Tooltail.Desktop.runtimeconfig.json",
        "coreclr.dll",
        "hostfxr.dll",
        "hostpolicy.dll",
        "PresentationFramework.dll",
    ];
}
