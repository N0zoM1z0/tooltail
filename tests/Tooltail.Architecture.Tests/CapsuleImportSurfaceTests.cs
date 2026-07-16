namespace Tooltail.Architecture.Tests;

public sealed class CapsuleImportSurfaceTests
{
    [Fact]
    public void DesktopRequiresSeparatePreviewImportGrantRebindAndRehearsalControls()
    {
        string home = File.ReadAllText(SourcePath(
            "src",
            "Tooltail.Desktop",
            "HomeWindow.xaml"));
        string controller = File.ReadAllText(SourcePath(
            "src",
            "Tooltail.Desktop",
            "Presentation",
            "FileApprenticeInteractionController.cs"));

        Assert.Contains("CanPreviewCapsuleImport", home, StringComparison.Ordinal);
        Assert.Contains("CanCommitCapsuleImport", home, StringComparison.Ordinal);
        Assert.Contains("CanRebindImportedSkill", home, StringComparison.Ordinal);
        Assert.Contains("Creates authority: {0}", home, StringComparison.Ordinal);
        Assert.Contains("PreviewCapsuleImportAsync", controller, StringComparison.Ordinal);
        Assert.Contains("CommitCapsuleImportAsync", controller, StringComparison.Ordinal);
        Assert.Contains("RebindNextImportedSkillAsync", controller, StringComparison.Ordinal);
        Assert.Contains("RehearseSkillAsync", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportPersistenceIsPristineAtomicAndCannotCreateAuthorityRows()
    {
        string persistence = File.ReadAllText(SourcePath(
            "src",
            "Tooltail.Infrastructure.Sqlite",
            "SqliteFileSkillStateStore.Capsules.cs"));

        Assert.Contains("RequirePristineCompanionAsync", persistence, StringComparison.Ordinal);
        string store = File.ReadAllText(SourcePath(
            "src",
            "Tooltail.Infrastructure.Sqlite",
            "SqliteFileSkillStateStore.cs"));
        Assert.Contains("BeginImmediateAsync", store, StringComparison.Ordinal);
        Assert.Contains("RollbackAsync", store, StringComparison.Ordinal);
        Assert.Contains("SkillLifecycleState.Stale", persistence, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO resource_grants", persistence, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO approvals", persistence, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO execution_plans", persistence, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO executions", persistence, StringComparison.Ordinal);
    }

    [Fact]
    public void FilePreviewReadsBoundedExactBytesAndNeverWritesSelectedFile()
    {
        string reader = File.ReadAllText(SourcePath(
            "src",
            "Tooltail.Features.FileSkills",
            "Continuity",
            "CapsuleImportFileWorkflowService.cs"));

        Assert.Contains("CompanionCapsuleMaximumBytes", reader, StringComparison.Ordinal);
        Assert.Contains("PathEntryExpectation.MustExist", reader, StringComparison.Ordinal);
        Assert.Contains("pathSafety.Revalidate", reader, StringComparison.Ordinal);
        Assert.Contains("SHA256.HashData", reader, StringComparison.Ordinal);
        Assert.DoesNotContain("FileMode.Create", reader, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Write", reader, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete", reader, StringComparison.Ordinal);
    }

    private static string SourcePath(params string[] segments) =>
        Path.Combine([RepositoryLayout.FindRoot(), .. segments]);
}
