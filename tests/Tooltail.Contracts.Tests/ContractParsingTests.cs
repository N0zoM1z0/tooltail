using System.Text;
using Tooltail.Contracts.AgentEvents;
using Tooltail.Contracts.Json;
using Tooltail.Contracts.Scopes;
using Tooltail.Contracts.Skills;

namespace Tooltail.Contracts.Tests;

public sealed class ContractParsingTests
{
    [Fact]
    public void ClosedParsersAcceptEveryBundledExample()
    {
        string exampleDirectory = Path.Combine(FindRepositoryRoot(), "docs", "examples");

        ContractParseResult<SkillSpecContract> skillResult =
            ContractJson.ParseSkillSpec(ReadBytes(exampleDirectory, "file-skill.example.json"));
        Assert.True(skillResult.IsSuccess, skillResult.Error?.ToString());
        Assert.True(ContractJson.ParseWindowLease(ReadBytes(exampleDirectory, "scope-lease.example.json")).IsSuccess);
        Assert.True(ContractJson.ParseCompanionCapsule(ReadBytes(exampleDirectory, "companion-capsule.example.json")).IsSuccess);

        foreach (string line in File.ReadLines(Path.Combine(exampleDirectory, "agent-events.example.jsonl")))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                Assert.True(ContractJson.ParseAgentEvent(Encoding.UTF8.GetBytes(line)).IsSuccess);
            }
        }
    }

    [Fact]
    public void ParserRejectsUnknownExecutableAction()
    {
        string json = ReadText("file-skill.example.json").Replace(
            "\"move_file\"",
            "\"execute_shell\"",
            StringComparison.Ordinal);

        ContractParseResult<SkillSpecContract> result =
            ContractJson.ParseSkillSpec(Encoding.UTF8.GetBytes(json));

        Assert.False(result.IsSuccess);
        Assert.Equal("contract.invalid_json", result.Error?.Code);
    }

    [Fact]
    public void ParserRejectsUnknownProperties()
    {
        string json = ReadText("scope-lease.example.json").Replace(
            "\"schemaVersion\": \"1.0\"",
            "\"schemaVersion\": \"1.0\", \"mutationAuthority\": true",
            StringComparison.Ordinal);

        ContractParseResult<WindowLeaseContract> result =
            ContractJson.ParseWindowLease(Encoding.UTF8.GetBytes(json));

        Assert.False(result.IsSuccess);
        Assert.Equal("contract.invalid_json", result.Error?.Code);
    }

    [Fact]
    public void ParserRejectsUnknownSchemaVersion()
    {
        string firstEvent = File.ReadLines(
                Path.Combine(FindRepositoryRoot(), "docs", "examples", "agent-events.example.jsonl"))
            .First();
        string json = firstEvent.Replace("\"1.0\"", "\"2.0\"", StringComparison.Ordinal);

        ContractParseResult<AgentEventContract> result =
            ContractJson.ParseAgentEvent(Encoding.UTF8.GetBytes(json));

        Assert.False(result.IsSuccess);
        Assert.Equal("contract.unsupported_schema_version", result.Error?.Code);
    }

    [Fact]
    public void AgentEventParserEnforcesByteLimitBeforeParsing()
    {
        byte[] oversized = new byte[ContractJson.AgentEventMaximumBytes + 1];
        Array.Fill(oversized, (byte)' ');

        ContractParseResult<AgentEventContract> result = ContractJson.ParseAgentEvent(oversized);

        Assert.False(result.IsSuccess);
        Assert.Equal("contract.too_large", result.Error?.Code);
    }

    private static byte[] ReadBytes(string directory, string fileName) =>
        File.ReadAllBytes(Path.Combine(directory, fileName));

    private static string ReadText(string fileName) =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "docs", "examples", fileName));

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

        throw new DirectoryNotFoundException("Could not locate Tooltail.sln from the contract test output directory.");
    }
}
