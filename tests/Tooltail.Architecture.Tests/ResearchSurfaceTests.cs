using System.Xml.Linq;

namespace Tooltail.Architecture.Tests;

public sealed class ResearchSurfaceTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly XNamespace Automation =
        "clr-namespace:System.Windows.Automation;assembly=PresentationCore";

    [Fact]
    public void HomeMakesDefaultOffConsentPreviewExportDeletionAndResetExplicit()
    {
        string source = File.ReadAllText(DesktopPath("HomeWindow.xaml"));
        XDocument home = XDocument.Parse(source);
        string[] requiredControls =
        [
            "Explicitly opt in to closed local research events",
            "Preview exact local research JSONL before export",
            "Export reviewed local research JSONL with CreateNew",
            "Delete and disable all local research data",
            "Start a fresh study session and non-destructive safe lab fixture",
            "Submit participant entered closed study rating",
        ];
        string[] buttons = home.Descendants(Presentation + "Button")
            .Select(button =>
                button.Attribute(Automation + "AutomationProperties.Name")?.Value ??
                string.Empty)
            .ToArray();

        Assert.All(requiredControls, control => Assert.Contains(control, buttons));
        Assert.Contains("OFF by default", source, StringComparison.Ordinal);
        Assert.Contains("There is no uploader or automatic network transfer", source, StringComparison.Ordinal);
        Assert.Contains("never removes the prior lab or its files", source, StringComparison.Ordinal);
        Assert.Contains(
            home.Descendants(Presentation + "TextBox"),
            textBox =>
                textBox.Attribute("IsReadOnly")?.Value == "True" &&
                textBox.Attribute(Automation + "AutomationProperties.Name")?.Value ==
                    "Exact bounded local research JSONL preview");
    }

    [Fact]
    public void ResearchBoundaryHasNoUploaderOrAuthorityShortcut()
    {
        string infrastructure = string.Join(
            '\n',
            Directory.EnumerateFiles(
                    SourcePath("Tooltail.Infrastructure.LocalResearch"),
                    "*.cs")
                .Select(File.ReadAllText));
        string recorder = File.ReadAllText(DesktopPath(
            "Presentation",
            "ResearchEventRecorder.cs"));
        string controller = File.ReadAllText(DesktopPath(
            "Presentation",
            "ResearchInteractionController.cs"));
        string[] forbidden =
        [
            "HttpClient",
            "System.Net",
            "WebRequest",
            "Socket",
            "PermissionGateway",
            "PlanApproval",
            "WindowLease",
            "LocalFolderGrant",
            "Process.Start",
        ];

        Assert.All(
            forbidden,
            value => Assert.DoesNotContain(
                value,
                infrastructure + recorder,
                StringComparison.Ordinal));
        Assert.Contains("Research is observational only", recorder, StringComparison.Ordinal);
        Assert.Contains("ResetSessionAsync", controller, StringComparison.Ordinal);
        Assert.Contains("RevokeFolderGrantAsync", controller, StringComparison.Ordinal);
        Assert.Contains("CreateSafeLabAsync", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete(", infrastructure, StringComparison.Ordinal);
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
        Path.Combine(
            [
                RepositoryLayout.FindRoot(),
                "src",
                .. segments,
            ]);
}
