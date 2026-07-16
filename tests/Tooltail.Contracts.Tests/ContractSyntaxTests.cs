using System.Text.Json;
using Json.Schema;
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
        Assert.Equal(5, schemas.Length);
        Assert.Equal(4, examples.Length);

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

    [Fact]
    public void BundledExamplesValidateAgainstDraft202012Schemas()
    {
        string root = FindRepositoryRoot();
        string schemaDirectory = Path.Combine(root, "docs", "schemas");
        string exampleDirectory = Path.Combine(root, "docs", "examples");
        SchemaRegistry registry = new();
        BuildOptions buildOptions = new() { SchemaRegistry = registry };

        Dictionary<string, JsonSchema> schemas = Directory
            .GetFiles(schemaDirectory, "*.json")
            .ToDictionary(
                static path => Path.GetFileName(path),
                path => JsonSchema.FromFile(path, buildOptions),
                StringComparer.Ordinal);
        EvaluationOptions evaluationOptions = new()
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = true,
        };

        AssertValid(
            schemas["skill-spec.schema.json"],
            File.ReadAllText(Path.Combine(exampleDirectory, "file-skill.example.json")),
            evaluationOptions);
        AssertValid(
            schemas["scope-lease.schema.json"],
            File.ReadAllText(Path.Combine(exampleDirectory, "scope-lease.example.json")),
            evaluationOptions);
        AssertValid(
            schemas["companion-capsule.schema.json"],
            File.ReadAllText(Path.Combine(exampleDirectory, "companion-capsule.example.json")),
            evaluationOptions);
        AssertValid(
            schemas["research-event.schema.json"],
            File.ReadAllText(Path.Combine(exampleDirectory, "research-event.example.json")),
            evaluationOptions);

        foreach (string line in File.ReadLines(Path.Combine(exampleDirectory, "agent-events.example.jsonl")))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                AssertValid(schemas["agent-event.schema.json"], line, evaluationOptions);
            }
        }
    }

    private static void AssertValid(JsonSchema schema, string json, EvaluationOptions options)
    {
        using JsonDocument instance = JsonDocument.Parse(json);
        EvaluationResults result = schema.Evaluate(instance.RootElement, options);
        Assert.True(result.IsValid, $"Schema evaluation failed: {result}");
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
