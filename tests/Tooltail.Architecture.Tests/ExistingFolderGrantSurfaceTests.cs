namespace Tooltail.Architecture.Tests;

public sealed class ExistingFolderGrantSurfaceTests
{
    [Fact]
    public void HomeSeparatesFolderSelectionPreviewAndExactConfirmation()
    {
        string home = File.ReadAllText(SourcePath(
            "src", "Tooltail.Desktop", "HomeWindow.xaml"));
        string codeBehind = File.ReadAllText(SourcePath(
            "src", "Tooltail.Desktop", "HomeWindow.xaml.cs"));

        Assert.Contains("Select existing local folder", home, StringComparison.Ordinal);
        Assert.Contains("Confirm exact folder grant", home, StringComparison.Ordinal);
        Assert.Contains("CanSelectExistingFolder", home, StringComparison.Ordinal);
        Assert.Contains("CanConfirmExistingFolderGrant", home, StringComparison.Ordinal);
        Assert.Contains("OpenFolderDialog", codeBehind, StringComparison.Ordinal);
        Assert.Contains("PreviewExistingFolderAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ConfirmExistingFolderGrantAsync", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingFolderConfirmationRevalidatesAndPersistsOnlyClosedAuthority()
    {
        string service = File.ReadAllText(SourcePath(
            "src", "Tooltail.Features.FileSkills", "Grants",
            "ExistingFolderGrantService.cs"));

        Assert.Contains("CaptureRoot(selectedPath)", service, StringComparison.Ordinal);
        Assert.Contains("CaptureRoot(\n            preview.Root.CanonicalPath)", service,
            StringComparison.Ordinal);
        Assert.Contains("root_identity_changed", service, StringComparison.Ordinal);
        Assert.Contains("active_grant_exists", service, StringComparison.Ordinal);
        Assert.Contains("rootProtector.Protect", service, StringComparison.Ordinal);
        Assert.Contains("LocalFolderGrantPolicy.FileApprenticeCapabilities", service,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Enumerate", service, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete", service, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Delete", service, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", service, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalRootProtectionIsWindowsPlatformOwnedAndCurrentUserScoped()
    {
        string platform = File.ReadAllText(SourcePath(
            "src", "Tooltail.Platform.Windows", "FileSystem",
            "WindowsLocalFolderRootProtector.cs"));
        string desktop = File.ReadAllText(SourcePath(
            "src", "Tooltail.Desktop", "Presentation",
            "SafeLabGrantService.cs"));

        Assert.Contains("CryptProtectData", platform, StringComparison.Ordinal);
        Assert.Contains("CryptUnprotectData", platform, StringComparison.Ordinal);
        Assert.Contains("CryptProtectUiForbidden", platform, StringComparison.Ordinal);
        Assert.DoesNotContain("CryptprotectLocalMachine", platform,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rootProtector.Unprotect", desktop, StringComparison.Ordinal);
        Assert.Contains("ProtectedCanonicalRoot", desktop, StringComparison.Ordinal);
        Assert.DoesNotContain("DllImport", desktop, StringComparison.Ordinal);
    }

    private static string SourcePath(params string[] segments) =>
        Path.Combine([RepositoryLayout.FindRoot(), .. segments]);
}
