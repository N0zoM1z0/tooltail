using System.Xml.Linq;

namespace Tooltail.Architecture.Tests;

public sealed class WindowShellSurfaceTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly XNamespace Automation =
        "clr-namespace:System.Windows.Automation;assembly=PresentationCore";

    [Fact]
    public void AmbientPetAndTetherDeclareNonActivatingTransparentSurfaces()
    {
        XElement pet = XDocument.Load(DesktopPath("PetWindow.xaml")).Root!;
        XElement tether = XDocument.Load(DesktopPath("TetherWindow.xaml")).Root!;

        AssertAmbient(pet, expectedHitTestVisible: null);
        AssertAmbient(tether, expectedHitTestVisible: "False");
        Assert.Equal("{Binding PetAccessibleName}",
            pet.Attribute(Automation + "AutomationProperties.Name")?.Value);
        Assert.Equal("Tooltail click-through context tether",
            tether.Attribute(Automation + "AutomationProperties.Name")?.Value);
        Assert.Contains(
            tether.Descendants(Presentation + "Rectangle"),
            rectangle => rectangle.Attribute("Stroke")?.Value.Contains(
                "SystemColors.HighlightBrushKey",
                StringComparison.Ordinal) == true);
    }

    [Fact]
    public void InspectorAndHomeExposeExactScopeAndEveryKeyboardCriticalControl()
    {
        string homeSource = File.ReadAllText(DesktopPath("HomeWindow.xaml"));
        string inspectorSource = File.ReadAllText(DesktopPath("InspectorWindow.xaml"));
        XDocument home = XDocument.Parse(homeSource);
        XDocument inspector = XDocument.Parse(inspectorSource);
        string[] requiredAccessibleControls =
        [
            "Refresh eligible window targets",
            "Attach to selected window as context",
            "Open exact window lease inspector",
            "Revoke active window lease",
            "Revoke exact active folder ResourceGrant",
            "Return Tooltail home and revoke context",
            "Pause current Tooltail execution",
            "Cancel current Tooltail execution",
            "Create a new Tooltail-owned safe lab and exact folder grant",
            "Capture baseline and start teaching observation",
            "Stop teaching and reconcile authoritative snapshots",
            "Compile reconciled teaching examples or submit typed clarifications",
            "Rehearse the Draft in a Tooltail-owned temporary root",
            "Approve the exact displayed fingerprint and execute once",
            "Plan Undo from the verified receipt and current snapshot",
            "Approve the exact recovery fingerprint and execute Undo once",
            "Create a parent-linked corrected Draft that requires new rehearsal",
            "Export validated companion history without authority",
            "Explicitly opt in to closed local research events",
            "Preview exact local research JSONL before export",
            "Export reviewed local research JSONL with CreateNew",
            "Delete and disable all local research data",
            "Start a fresh study session and non-destructive safe lab fixture",
            "Submit participant entered closed study rating",
        ];

        string[] homeControls = home
            .Descendants(Presentation + "Button")
            .Select(button =>
                button.Attribute(Automation + "AutomationProperties.Name")?.Value ?? string.Empty)
            .ToArray();
        Assert.All(requiredAccessibleControls, control => Assert.Contains(control, homeControls));
        Assert.Contains("It is not an operating-system sandbox", homeSource, StringComparison.Ordinal);
        Assert.Contains("grants no file, UI, network, shell, model, or process action", homeSource, StringComparison.Ordinal);
        Assert.Contains("File Apprentice — local persisted state", homeSource, StringComparison.Ordinal);
        Assert.Contains("No login, model key, chat setup, telemetry, or customization is required", homeSource, StringComparison.Ordinal);
        Assert.Contains("Folder authority", homeSource, StringComparison.Ordinal);
        Assert.Contains("Recovery", homeSource, StringComparison.Ordinal);
        Assert.Contains(
            "Committed File Apprentice body truth",
            homeSource,
            StringComparison.Ordinal);
        Assert.Contains("Exact contract", inspectorSource, StringComparison.Ordinal);
        Assert.Contains("HWND", inspectorSource, StringComparison.Ordinal);
        Assert.Contains("Process started UTC", inspectorSource, StringComparison.Ordinal);
        Assert.Contains("IsReadOnly=\"True\"", inspectorSource, StringComparison.Ordinal);
        Assert.Contains(
            inspector.Descendants(Presentation + "ComboBox"),
            combo => combo.Attribute(Automation + "AutomationProperties.Name")?.Value ==
                "Inspector eligible window targets");
        Assert.Contains(
            inspector.Descendants(Presentation + "Button"),
            button => button.Attribute(Automation + "AutomationProperties.Name")?.Value ==
                "Inspector revoke exact active folder ResourceGrant");
    }

    [Fact]
    public void DragCompositionUsesPhysicalPointerAndExplicitDropWithoutFocusOrInputApis()
    {
        string petCode = File.ReadAllText(DesktopPath("PetWindow.xaml.cs"));
        string tetherCode = File.ReadAllText(DesktopPath("TetherWindow.xaml.cs"));
        string appCode = File.ReadAllText(DesktopPath("App.xaml.cs"));
        string[] requiredPetOperations =
        [
            "IPhysicalPointerSource",
            "BeginDragAsync",
            "PreviewAtAsync",
            "DropAsync",
            "CancelDragAsync",
            "AmbientWindowMessagePolicy",
            "MoveNoActivate",
        ];
        string[] forbiddenDesktopOperations =
        [
            "DragMove(",
            "SetForegroundWindow",
            "SendInput",
            "SetCursorPos",
            "Process.Start",
            "PermissionGateway",
            "LocalFolderGrant",
        ];

        Assert.All(requiredPetOperations,
            operation => Assert.Contains(operation, petCode, StringComparison.Ordinal));
        Assert.Contains("PlaceNoActivate", tetherCode, StringComparison.Ordinal);
        Assert.Contains("AmbientWindowSurfaceKind.Tether", tetherCode, StringComparison.Ordinal);
        Assert.Contains("--window-shell-smoke-test", appCode, StringComparison.Ordinal);
        Assert.All(
            forbiddenDesktopOperations,
            operation => Assert.DoesNotContain(
                operation,
                petCode + tetherCode,
                StringComparison.Ordinal));
    }

    private static void AssertAmbient(XElement window, string? expectedHitTestVisible)
    {
        Assert.Equal("True", window.Attribute("AllowsTransparency")?.Value);
        Assert.Equal("Transparent", window.Attribute("Background")?.Value);
        Assert.Equal("False", window.Attribute("ShowInTaskbar")?.Value);
        Assert.Equal("False", window.Attribute("ShowActivated")?.Value);
        Assert.Equal("True", window.Attribute("Topmost")?.Value);
        if (expectedHitTestVisible is not null)
        {
            Assert.Equal(expectedHitTestVisible, window.Attribute("IsHitTestVisible")?.Value);
        }
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
