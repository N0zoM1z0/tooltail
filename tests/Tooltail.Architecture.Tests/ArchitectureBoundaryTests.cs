using System.Xml.Linq;

namespace Tooltail.Architecture.Tests;

public sealed class ArchitectureBoundaryTests
{
    [Fact]
    public void ProductionProjectGraphMatchesReviewedBoundaries()
    {
        string root = RepositoryLayout.FindRoot();
        IReadOnlyDictionary<string, IReadOnlySet<string>> graph = ProjectGraphRules.ReadProductionGraph(root);

        IReadOnlyList<string> errors = ProjectGraphRules.Validate(graph);

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void DomainAndContractsHaveNoPackageDependencies()
    {
        string root = RepositoryLayout.FindRoot();

        IReadOnlyList<string> domainPackages = ProjectGraphRules.ReadPackageReferences(
            Path.Combine(root, "src", "Tooltail.Domain", "Tooltail.Domain.csproj"));
        IReadOnlyList<string> contractPackages = ProjectGraphRules.ReadPackageReferences(
            Path.Combine(root, "src", "Tooltail.Contracts", "Tooltail.Contracts.csproj"));

        Assert.Empty(domainPackages);
        Assert.Empty(contractPackages);
    }

    [Fact]
    public void WindowsTargetingIsConfinedToPlatformAndDesktopProjects()
    {
        string root = RepositoryLayout.FindRoot();
        string sourceRoot = Path.Combine(root, "src");

        foreach (string projectPath in Directory.EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories))
        {
            string projectName = Path.GetFileNameWithoutExtension(projectPath);
            XDocument project = XDocument.Load(projectPath, LoadOptions.None);
            string targetFramework = project.Descendants("TargetFramework").Single().Value;
            bool shouldTargetWindows = projectName is "Tooltail.Platform.Windows" or "Tooltail.Desktop";

            Assert.Equal(shouldTargetWindows, targetFramework.Contains("-windows", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void NativeInteropDeclarationsAreConfinedToWindowsPlatformProject()
    {
        string root = RepositoryLayout.FindRoot();
        string sourceRoot = Path.Combine(root, "src");

        string[] nativeFiles = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(static path =>
            {
                string source = File.ReadAllText(path);
                return source.Contains("[DllImport(", StringComparison.Ordinal) ||
                    source.Contains("[LibraryImport(", StringComparison.Ordinal);
            })
            .ToArray();

        Assert.All(
            nativeFiles,
            static path => Assert.Contains(
                $"{Path.DirectorySeparatorChar}Tooltail.Platform.Windows{Path.DirectorySeparatorChar}",
                path,
                StringComparison.Ordinal));
    }

    [Fact]
    public void RuleEngineRejectsAForbiddenDomainDependency()
    {
        Dictionary<string, IReadOnlySet<string>> graph = ProjectGraphRules
            .ReadProductionGraph(RepositoryLayout.FindRoot())
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        graph["Tooltail.Domain"] = new HashSet<string>(["Tooltail.Application"], StringComparer.Ordinal);

        IReadOnlyList<string> errors = ProjectGraphRules.Validate(graph);

        Assert.Contains(
            errors,
            static error => error.Contains(
                "Tooltail.Domain' must not reference 'Tooltail.Application",
                StringComparison.Ordinal));
    }

    [Fact]
    public void FileMutationApisAreConfinedToReviewedExecutionAndOwnedTempSurfaces()
    {
        string root = RepositoryLayout.FindRoot();
        string featureRoot = Path.Combine(root, "src", "Tooltail.Features.FileSkills");
        string[] mutationTokens =
        [
            "Directory.CreateDirectory(",
            "Directory.Delete(",
            "File.Copy(",
            "File.Delete(",
            "File.Move(",
            "File.Replace(",
            "Process.Start(",
        ];
        string[] mutationFiles = Directory
            .EnumerateFiles(featureRoot, "*.cs", SearchOption.AllDirectories)
            .Where(static path =>
                !path.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            .Where(path => mutationTokens.Any(
                token => File.ReadAllText(path).Contains(token, StringComparison.Ordinal)))
            .Select(static path => Path.GetFileName(path)!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "PortableFixtureFileMutationEngine.cs",
                "RehearsalFixtureStager.cs",
                "RehearsalWorkspace.cs",
            ],
            mutationFiles);
    }

    [Fact]
    public void DesktopCompositionUsesNativeMutationEngineWithoutPortableFallback()
    {
        string root = RepositoryLayout.FindRoot();
        string application = File.ReadAllText(
            Path.Combine(root, "src", "Tooltail.Desktop", "App.xaml.cs"));
        string desktopRoot = Path.Combine(root, "src", "Tooltail.Desktop");
        string[] desktopSources = Directory
            .EnumerateFiles(desktopRoot, "*.cs", SearchOption.AllDirectories)
            .Where(static path =>
                !path.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToArray();

        Assert.Contains("IFileMutationEngine", application, StringComparison.Ordinal);
        Assert.Contains(
            "WindowsHandleBoundFileMutationEngine",
            application,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            desktopSources,
            static source => source.Contains(
                "PortableFixtureFileMutationEngine",
                StringComparison.Ordinal));
    }

    [Fact]
    public void AgentProcessApisAreConfinedToReviewedCodexBoundary()
    {
        string sourceRoot = Path.Combine(
            RepositoryLayout.FindRoot(),
            "src");
        string[] processTokens =
        [
            "new ProcessStartInfo",
            "new Process {",
            "Kill(entireProcessTree:",
        ];
        string[] processFiles = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(static path =>
                !path.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            .Where(path => processTokens.Any(
                token => File.ReadAllText(path).Contains(token, StringComparison.Ordinal)))
            .Select(static path => Path.GetFileName(path)!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "CodexChildProcess.cs",
                "CodexExecRunner.cs",
            ],
            processFiles);
    }

    [Fact]
    public void ProductionAgentAdapterDoesNotReferencePrivateCodexState()
    {
        string adapterRoot = Path.Combine(
            RepositoryLayout.FindRoot(),
            "src",
            "Tooltail.Adapters.AgentEvents");
        string[] forbiddenPrivateStateTokens =
        [
            ".codex/sessions",
            "codex_home",
            "rollout",
            "session-",
            "app-server",
        ];
        List<string> violations = [];
        foreach (string path in Directory.EnumerateFiles(
                     adapterRoot,
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            if (path.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            {
                continue;
            }

            string source = File.ReadAllText(path);
            foreach (string token in forbiddenPrivateStateTokens)
            {
                if (source.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{Path.GetFileName(path)}:{token}");
                }
            }
        }

        Assert.Empty(violations);
    }
}
