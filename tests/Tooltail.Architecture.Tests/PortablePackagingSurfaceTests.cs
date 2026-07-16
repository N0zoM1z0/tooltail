namespace Tooltail.Architecture.Tests;

public sealed class PortablePackagingSurfaceTests
{
    [Fact]
    public void PublishProfileIsSelfContainedUntrimmedWinX64WithoutSigning()
    {
        string profile = File.ReadAllText(DesktopPath(
            "Properties",
            "PublishProfiles",
            "win-x64.pubxml"));
        string project = File.ReadAllText(DesktopPath("Tooltail.Desktop.csproj"));
        string manifest = File.ReadAllText(DesktopPath("app.manifest"));

        Assert.Contains("<RuntimeIdentifier>win-x64</RuntimeIdentifier>", profile);
        Assert.Contains("<SelfContained>true</SelfContained>", profile);
        Assert.Contains("<PublishTrimmed>false</PublishTrimmed>", profile);
        Assert.Contains("<PublishSingleFile>false</PublishSingleFile>", profile);
        Assert.Contains("<PublishReadyToRun>false</PublishReadyToRun>", profile);
        Assert.Contains("<VersionPrefix>0.1.0</VersionPrefix>", project);
        Assert.Contains(
            "requestedExecutionLevel level=\"asInvoker\" uiAccess=\"false\"",
            manifest,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Sign", profile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Installer", profile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Update", profile, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackagingScriptUsesFixedOutputsAndNeverDeletesOrPublishes()
    {
        string script = File.ReadAllText(SourcePath("eng", "package-portable.ps1"));
        string tool = File.ReadAllText(SourcePath(
            "tools",
            "Tooltail.ReleaseAudit",
            "PortablePackageApplication.cs"));

        Assert.Contains("artifacts/portable", script, StringComparison.Ordinal);
        Assert.Contains("--self-contained true", script, StringComparison.Ordinal);
        Assert.Contains("--locked-mode", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--force", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("publish release", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FileMode.CreateNew", tool, StringComparison.Ordinal);
        Assert.Contains("Directory.Delete(programRoot, recursive: true)", tool);
        Assert.Contains("ValidateRemovalBoundary", tool, StringComparison.Ordinal);
        Assert.Contains("program_directory_only", tool, StringComparison.Ordinal);
        Assert.Contains("%LOCALAPPDATA%\\\\Tooltail", tool, StringComparison.Ordinal);
    }

    private static string DesktopPath(params string[] segments) =>
        Path.Combine(
            [
                RepositoryLayout.FindRoot(),
                "src",
                "Tooltail.Desktop",
                .. segments,
            ]);

    private static string SourcePath(params string[] segments) =>
        Path.Combine([RepositoryLayout.FindRoot(), .. segments]);
}
