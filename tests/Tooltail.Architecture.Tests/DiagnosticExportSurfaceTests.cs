namespace Tooltail.Architecture.Tests;

public sealed class DiagnosticExportSurfaceTests
{
    [Fact]
    public void HomeRequiresPreviewBeforeExactDiagnosticExport()
    {
        string home = File.ReadAllText(SourcePath(
            "src",
            "Tooltail.Desktop",
            "HomeWindow.xaml"));

        Assert.Contains("Preview redacted diagnostics", home, StringComparison.Ordinal);
        Assert.Contains("Export exact preview", home, StringComparison.Ordinal);
        Assert.Contains("CanPreview", home, StringComparison.Ordinal);
        Assert.Contains("CanExport", home, StringComparison.Ordinal);
        Assert.Contains("Exact redacted diagnostic JSON preview", home, StringComparison.Ordinal);
        Assert.Contains("no path, filename, title, content, prompt, transcript", home,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiagnosticBuilderHasNoRawInputFieldsAndReaderIsStrict()
    {
        string builder = File.ReadAllText(SourcePath(
            "src",
            "Tooltail.Application",
            "Diagnostics",
            "DiagnosticExport.cs"));

        Assert.Contains("UnmappedMemberHandling.Disallow", builder, StringComparison.Ordinal);
        Assert.Contains("MaximumBytes = 64 * 1024", builder, StringComparison.Ordinal);
        Assert.Contains("ContainsRawPaths: false", builder, StringComparison.Ordinal);
        Assert.Contains("ContainsFileNames: false", builder, StringComparison.Ordinal);
        Assert.DoesNotContain("string CanonicalPath", builder, StringComparison.Ordinal);
        Assert.DoesNotContain("string DisplayName", builder, StringComparison.Ordinal);
        Assert.DoesNotContain("Guid CompanionId", builder, StringComparison.Ordinal);
        Assert.DoesNotContain("string WindowTitle", builder, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticWriterUsesOwnedCreateNewAndNoNetworkOrDeletion()
    {
        string workflow = File.ReadAllText(SourcePath(
            "src",
            "Tooltail.Desktop",
            "Presentation",
            "DiagnosticExportWorkflowService.cs"));

        Assert.Contains("PathEntryExpectation.MustNotExist", workflow,
            StringComparison.Ordinal);
        Assert.Contains("FileMode.CreateNew", workflow, StringComparison.Ordinal);
        Assert.Contains("SHA256.HashData", workflow, StringComparison.Ordinal);
        Assert.Contains("DiagnosticExportBuilder.Parse", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Delete", workflow, StringComparison.Ordinal);
    }

    private static string SourcePath(params string[] segments) =>
        Path.Combine([RepositoryLayout.FindRoot(), .. segments]);
}
