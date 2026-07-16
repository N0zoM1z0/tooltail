using System.Xml.Linq;

namespace Tooltail.Architecture.Tests;

public sealed class LocalDataLifecycleSurfaceTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly XNamespace Automation =
        "clr-namespace:System.Windows.Automation;assembly=PresentationCore";

    [Fact]
    public void HomeRequiresPreviewAndExactPhraseAndDisclosesBothDataBoundaries()
    {
        XDocument home = XDocument.Load(DesktopPath("HomeWindow.xaml"));
        string source = home.ToString(SaveOptions.DisableFormatting);
        string[] controlNames = home.Descendants()
            .Select(element =>
                element.Attribute(Automation + "AutomationProperties.Name")?.Value ??
                string.Empty)
            .ToArray();

        Assert.Contains(
            "Prepare exact Tooltail local product state deletion preview",
            controlNames);
        Assert.Contains(
            "Exact phrase confirming Tooltail local product state deletion",
            controlNames);
        Assert.Contains(
            "Delete reviewed Tooltail local product state and exit",
            controlNames);
        Assert.Contains("Will be deleted", source, StringComparison.Ordinal);
        Assert.Contains("Will be preserved", source, StringComparison.Ordinal);
        Assert.Contains("never removes safe labs", source, StringComparison.Ordinal);
        Assert.Contains("remain in local SQLite", source, StringComparison.Ordinal);

        XElement confirmation = Assert.Single(
            home.Descendants(Presentation + "TextBox"),
            textBox =>
                textBox.Attribute(Automation + "AutomationProperties.Name")?.Value ==
                "Exact phrase confirming Tooltail local product state deletion");
        Assert.Contains(
            "UpdateSourceTrigger=PropertyChanged",
            confirmation.Attribute("Text")?.Value,
            StringComparison.Ordinal);

        XElement delete = Assert.Single(
            home.Descendants(Presentation + "Button"),
            button =>
                button.Attribute(Automation + "AutomationProperties.Name")?.Value ==
                "Delete reviewed Tooltail local product state and exit");
        Assert.Equal("{Binding CanDelete}", delete.Attribute("IsEnabled")?.Value);
    }

    [Fact]
    public void OwnedDeletionSurfaceHasFixedFilesAndNoRecursiveOrEnumeratedRemoval()
    {
        string service = File.ReadAllText(SourcePath(
            "Tooltail.Infrastructure.Sqlite",
            "LocalStateDeletionService.cs"));
        string controller = File.ReadAllText(DesktopPath(
            "Presentation",
            "LocalDataLifecycleController.cs"));

        Assert.Equal(1, Count(service, "File.Delete("));
        Assert.DoesNotContain("Directory.Delete(", service, StringComparison.Ordinal);
        Assert.DoesNotContain("EnumerateFiles(", service, StringComparison.Ordinal);
        Assert.Contains("tooltail.db", service, StringComparison.Ordinal);
        Assert.Contains("local-state-deletion.intent.json", service, StringComparison.Ordinal);
        Assert.Contains("FileMode.CreateNew", service, StringComparison.Ordinal);

        int revoke = controller.IndexOf("RevokeFolderGrantAsync", StringComparison.Ordinal);
        int research = controller.IndexOf("researchStore.DeleteAllAsync", StringComparison.Ordinal);
        int product = controller.IndexOf("deletion.DeleteAsync", StringComparison.Ordinal);
        Assert.True(revoke >= 0 && revoke < research && research < product);
    }

    [Fact]
    public void StartupCompletesDeletionRecoveryBeforeOpeningSqlite()
    {
        string app = File.ReadAllText(DesktopPath("App.xaml.cs"));
        int recovery = app.IndexOf(".RecoverPendingDeletion();", StringComparison.Ordinal);
        int databaseInitialization = app.IndexOf(
            "GetRequiredService<TooltailSqliteDatabase>()\n                .InitializeAsync()",
            StringComparison.Ordinal);

        Assert.True(recovery >= 0 && recovery < databaseInitialization);
        Assert.Contains(
            "It did not open or replace the product database",
            app,
            StringComparison.Ordinal);
    }

    private static int Count(string source, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
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
