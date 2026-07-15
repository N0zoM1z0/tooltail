using System.Xml.Linq;

namespace Tooltail.Architecture.Tests;

internal static class ProjectGraphRules
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedReferences =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["Tooltail.Domain"] = NewSet(),
            ["Tooltail.Contracts"] = NewSet(),
            ["Tooltail.Application"] = NewSet("Tooltail.Domain", "Tooltail.Contracts"),
            ["Tooltail.Infrastructure.Sqlite"] = NewSet("Tooltail.Application", "Tooltail.Domain"),
            ["Tooltail.Platform.Windows"] = NewSet("Tooltail.Application", "Tooltail.Domain"),
            ["Tooltail.Features.FileSkills"] = NewSet("Tooltail.Application", "Tooltail.Domain", "Tooltail.Contracts"),
            ["Tooltail.Adapters.AgentEvents"] = NewSet("Tooltail.Application", "Tooltail.Domain", "Tooltail.Contracts"),
            ["Tooltail.Desktop"] = NewSet(
                "Tooltail.Application",
                "Tooltail.Infrastructure.Sqlite",
                "Tooltail.Platform.Windows",
                "Tooltail.Features.FileSkills",
                "Tooltail.Adapters.AgentEvents"),
        };

    public static IReadOnlyDictionary<string, IReadOnlySet<string>> ReadProductionGraph(string repositoryRoot)
    {
        string sourceRoot = Path.Combine(repositoryRoot, "src");
        Dictionary<string, IReadOnlySet<string>> graph = new(StringComparer.Ordinal);

        foreach (string projectPath in Directory.EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories))
        {
            XDocument project = XDocument.Load(projectPath, LoadOptions.None);
            HashSet<string> references = project
                .Descendants("ProjectReference")
                .Select(static element => element.Attribute("Include")?.Value)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => Path.GetFileNameWithoutExtension(value!.Replace('\\', '/')))
                .ToHashSet(StringComparer.Ordinal);

            graph.Add(Path.GetFileNameWithoutExtension(projectPath), references);
        }

        return graph;
    }

    public static IReadOnlyList<string> Validate(
        IReadOnlyDictionary<string, IReadOnlySet<string>> graph)
    {
        List<string> errors = [];

        foreach ((string project, IReadOnlySet<string> allowed) in AllowedReferences)
        {
            if (!graph.TryGetValue(project, out IReadOnlySet<string>? actual))
            {
                errors.Add($"Required production project '{project}' is missing.");
                continue;
            }

            foreach (string reference in actual)
            {
                if (!allowed.Contains(reference))
                {
                    errors.Add($"Project '{project}' must not reference '{reference}'.");
                }
            }
        }

        foreach (string unexpected in graph.Keys.Except(AllowedReferences.Keys, StringComparer.Ordinal))
        {
            errors.Add($"Production project '{unexpected}' has no reviewed architecture rule.");
        }

        return errors;
    }

    public static IReadOnlyList<string> ReadPackageReferences(string projectPath)
    {
        XDocument project = XDocument.Load(projectPath, LoadOptions.None);
        return project
            .Descendants("PackageReference")
            .Select(static element => element.Attribute("Include")?.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .ToArray();
    }

    private static HashSet<string> NewSet(params string[] values) =>
        values.ToHashSet(StringComparer.Ordinal);
}
