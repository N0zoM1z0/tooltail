using System.Xml.Linq;

namespace Tooltail.Architecture.Tests;

public sealed class SkillCardSurfaceTests
{
    [Fact]
    public void ReusableSkillCardSurfaceKeepsRequiredSectionsAndAccessibleNames()
    {
        string root = RepositoryLayout.FindRoot();
        string xamlPath = Path.Combine(
            root,
            "src",
            "Tooltail.Desktop",
            "Controls",
            "SkillCardControl.xaml");
        XDocument document = XDocument.Load(xamlPath, LoadOptions.None);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace automation =
            "clr-namespace:System.Windows.Automation;assembly=PresentationCore";
        XElement control = document.Root!;
        string[] expectedSections =
        [
            "Name",
            "When",
            "Where",
            "Matches",
            "Variables",
            "Do",
            "Sample before and after",
            "Always",
            "Never",
            "Ask me when",
            "Success means",
            "Learned from",
            "Version and compatibility",
            "Evidence",
            "Semantic diff from parent version",
            "Actions",
        ];
        XElement[] sections = control
            .Descendants(presentation + "GroupBox")
            .ToArray();

        Assert.Equal(
            expectedSections,
            sections.Select(static section => section.Attribute("Header")!.Value));
        Assert.All(
            sections,
            section => Assert.False(string.IsNullOrWhiteSpace(
                section.Attribute(automation + "AutomationProperties.Name")?.Value)));
        Assert.Equal(
            "Skill Card inspector",
            control.Attribute(automation + "AutomationProperties.Name")?.Value);

        XElement nameEditor = Assert.Single(
            control.Descendants(presentation + "TextBox"));
        Assert.Equal(
            "Editable skill name",
            nameEditor.Attribute(automation + "AutomationProperties.Name")?.Value);
        Assert.Equal(
            "True",
            nameEditor.Attribute(automation + "AutomationProperties.IsRequiredForForm")?.Value);

        XElement actionButtonTemplate = Assert.Single(
            control.Descendants(presentation + "Button"));
        Assert.Equal(
            "{Binding AutomationName}",
            actionButtonTemplate.Attribute(automation + "AutomationProperties.Name")?.Value);
        Assert.Equal("OnActionClick", actionButtonTemplate.Attribute("Click")?.Value);
    }

    [Fact]
    public void SkillCardControlOnlyRaisesIntentAndOwnsNoExecutionAuthority()
    {
        string codeBehind = File.ReadAllText(Path.Combine(
            RepositoryLayout.FindRoot(),
            "src",
            "Tooltail.Desktop",
            "Controls",
            "SkillCardControl.xaml.cs"));

        Assert.Contains("ActionRequestedEvent", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("PermissionGateway", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalFolderGrant", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecutionPlan", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("System.IO", codeBehind, StringComparison.Ordinal);

        string appCode = File.ReadAllText(Path.Combine(
            RepositoryLayout.FindRoot(),
            "src",
            "Tooltail.Desktop",
            "App.xaml.cs"));
        Assert.Contains("--skill-card-smoke-test", appCode, StringComparison.Ordinal);
    }
}
