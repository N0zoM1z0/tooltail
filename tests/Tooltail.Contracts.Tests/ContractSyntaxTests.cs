using System.Text.Json;
using Tooltail.Contracts;

namespace Tooltail.Contracts.Tests;

public sealed class ContractSyntaxTests
{
    [Fact]
    public void ContractAssemblyHasExpectedIdentity()
    {
        Assert.Equal("Tooltail.Contracts", typeof(ContractsAssembly).Assembly.GetName().Name);
    }

    [Fact]
    public void BundledSchemasAndExamplesContainValidJson()
    {
        string root = FindRepositoryRoot();
        string schemaDirectory = Path.Combine(root, "docs", "schemas");
        string exampleDirectory = Path.Combine(root, "docs", "examples");

        string[] schemas = Directory.GetFiles(schemaDirectory, "*.json");
        string[] examples = Directory.GetFiles(exampleDirectory, "*.json");
        Assert.Equal(4, schemas.Length);
        Assert.Equal(3, examples.Length);

        foreach (string path in schemas.Concat(examples))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        }

        string eventPath = Path.Combine(exampleDirectory, "agent-events.example.jsonl");
        string[] lines = File.ReadLines(eventPath).Where(static line => !string.IsNullOrWhiteSpace(line)).ToArray();
        Assert.Equal(7, lines.Length);
        foreach (string line in lines)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tooltail.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Tooltail.sln from the test output directory.");
    }
}
