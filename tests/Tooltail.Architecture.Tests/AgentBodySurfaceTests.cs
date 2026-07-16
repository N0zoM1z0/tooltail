using System.Xml.Linq;

namespace Tooltail.Architecture.Tests;

public sealed class AgentBodySurfaceTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly XNamespace Automation =
        "clr-namespace:System.Windows.Automation;assembly=PresentationCore";

    [Fact]
    public void BodyUsesOriginalStaticVectorGeometryForEveryCanonicalState()
    {
        string path = DesktopPath("Controls", "AgentBodyControl.xaml");
        XDocument document = XDocument.Load(path, LoadOptions.None);
        XElement root = document.Root!;
        string source = File.ReadAllText(path);
        string[] requiredStates =
        [
            "HomeIdle",
            "ScopedIdle",
            "Observing",
            "Working",
            "ParallelWork",
            "NeedsInput",
            "Blocked",
            "CompletedReceipt",
            "Failed",
            "PausedOrCancelled",
            "PermissionRevoked",
            "Disconnected",
        ];
        string[] forbiddenAssetOrMotionElements =
        [
            "Image",
            "MediaElement",
            "Storyboard",
            "DoubleAnimation",
            "ColorAnimation",
            "ObjectAnimationUsingKeyFrames",
        ];

        int geometryCount = root.Descendants().Count(element =>
            element.Name == Presentation + "Path" ||
            element.Name == Presentation + "Ellipse" ||
            element.Name == Presentation + "Rectangle" ||
            element.Name == Presentation + "Polygon");
        Assert.True(geometryCount >= 25, $"Expected rich original vector geometry, found {geometryCount} shapes.");
        Assert.All(
            requiredStates,
            state => Assert.Contains(
                $"CompanionBodyState.{state}",
                source,
                StringComparison.Ordinal));
        Assert.All(
            forbiddenAssetOrMotionElements,
            elementName => Assert.Empty(root.Descendants(Presentation + elementName)));
        Assert.Contains("SystemColors.WindowBrushKey", source, StringComparison.Ordinal);
        Assert.Contains("SystemColors.WindowTextBrushKey", source, StringComparison.Ordinal);
        Assert.Contains("SystemColors.HighlightBrushKey", source, StringComparison.Ordinal);
        Assert.Contains("ReducedMotion", File.ReadAllText(
            DesktopPath("Controls", "AgentBodyControl.xaml.cs")), StringComparison.Ordinal);
        Assert.Equal(
            "{Binding AccessibleName, ElementName=Root}",
            root.Attribute(Automation + "AutomationProperties.Name")?.Value);
    }

    [Fact]
    public void InspectorExposesRationaleTimelineAuthorityAndAccessibleDevControls()
    {
        string path = DesktopPath("MainWindow.xaml");
        XDocument document = XDocument.Load(path, LoadOptions.None);
        XElement root = document.Root!;
        string[] expectedGroups =
        [
            "Why this state",
            "Adapter",
            "Active normalized facts",
            "Scope and authority",
            "Accessibility",
        ];
        XElement[] groups = root.Descendants(Presentation + "GroupBox").ToArray();

        Assert.Equal(
            expectedGroups,
            groups.Select(static group => group.Attribute("Header")!.Value));
        Assert.All(
            groups,
            group => Assert.False(string.IsNullOrWhiteSpace(
                group.Attribute(Automation + "AutomationProperties.Name")?.Value)));
        Assert.Contains(
            root.Descendants(Presentation + "ListView"),
            list => list.Attribute(Automation + "AutomationProperties.Name")?.Value ==
                "Normalized event rows");

        string[] accessibleControls = root
            .Descendants()
            .Where(element => element.Name == Presentation + "Button" ||
                element.Name == Presentation + "ComboBox")
            .Select(element =>
                element.Attribute(Automation + "AutomationProperties.Name")?.Value ?? string.Empty)
            .ToArray();
        Assert.Contains("Simulator trace", accessibleControls);
        Assert.Contains("Playback speed", accessibleControls);
        Assert.Contains("Play or pause simulator trace", accessibleControls);
        Assert.Contains("Advance one simulator event", accessibleControls);
        Assert.Contains("Reset simulator playback", accessibleControls);

        string source = File.ReadAllText(path);
        Assert.Contains("IsDevelopmentPanelVisible", source, StringComparison.Ordinal);
        Assert.Contains("CurrentReasonCode", source, StringComparison.Ordinal);
        Assert.Contains("EvidenceSummary", source, StringComparison.Ordinal);
        Assert.Contains("AuthoritySummary", source, StringComparison.Ordinal);
        Assert.Contains("ReducedMotion", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopPlaybackOwnsPresentationOnlyAndHasReleaseSmokeMode()
    {
        string viewModel = File.ReadAllText(
            DesktopPath("Presentation", "AgentBodyWorkbenchViewModel.cs"));
        string windowCode = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        string appCode = File.ReadAllText(DesktopPath("App.xaml.cs"));
        string[] forbiddenAuthorityTokens =
        [
            "PermissionGateway",
            "LocalFolderGrant",
            "ExecutionPlan",
            "FileSkillExecutor",
            "Process.Start",
            "System.IO",
        ];

        Assert.Contains("#if DEBUG", viewModel, StringComparison.Ordinal);
        Assert.Contains("SetPlaybackActive", viewModel, StringComparison.Ordinal);
        Assert.Contains("StepForward", viewModel, StringComparison.Ordinal);
        Assert.Contains("DispatcherTimer", windowCode, StringComparison.Ordinal);
        Assert.Contains("--agent-body-smoke-test", appCode, StringComparison.Ordinal);
        Assert.All(
            forbiddenAuthorityTokens,
            token => Assert.DoesNotContain(token, viewModel, StringComparison.Ordinal));
        Assert.All(
            forbiddenAuthorityTokens,
            token => Assert.DoesNotContain(token, windowCode, StringComparison.Ordinal));
    }

    [Fact]
    public void FileApprenticeBodyProjectionCannotInvokeAnEffect()
    {
        string viewModel = File.ReadAllText(
            DesktopPath("Presentation", "FileApprenticeViewModel.cs"));
        string petProjection = File.ReadAllText(
            DesktopPath("Presentation", "WindowLeaseViewModel.cs"));
        string control = File.ReadAllText(
            DesktopPath("Controls", "AgentBodyControl.xaml.cs"));
        string combinedProjection = viewModel + petProjection + control;
        string[] forbiddenEffectBoundaries =
        [
            "PermissionGateway",
            "FileSkillExecutor",
            "FileRecoveryExecutor",
            "File.Move(",
            "File.Copy(",
            "Directory.Move(",
            "Process.Start(",
        ];

        Assert.Contains("CompanionActivityProjector.Project", viewModel, StringComparison.Ordinal);
        Assert.Contains("fileApprentice.CurrentBody", petProjection, StringComparison.Ordinal);
        Assert.All(
            forbiddenEffectBoundaries,
            boundary => Assert.DoesNotContain(
                boundary,
                combinedProjection,
                StringComparison.Ordinal));
    }

    private static string DesktopPath(params string[] segments) =>
        Path.Combine(
            [
                RepositoryLayout.FindRoot(),
                "src",
                "Tooltail.Desktop",
                .. segments,
            ]);
}
