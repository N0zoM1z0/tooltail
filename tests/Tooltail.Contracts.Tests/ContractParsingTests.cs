using System.Text;
using Tooltail.Contracts.AgentEvents;
using Tooltail.Contracts.Json;
using Tooltail.Contracts.Research;
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
        Assert.True(ContractJson.ParseResearchEvent(ReadBytes(exampleDirectory, "research-event.example.json")).IsSuccess);

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

    [Fact]
    public void ResearchEventParserRejectsRawFieldsInvalidTokensAndNonUtcTime()
    {
        string valid = ReadText("research-event.example.json");
        string rawPath = valid.Replace(
            "\"reasonCode\":",
            "\"rawPath\": \"C:\\\\Users\\\\Alice\", \"reasonCode\":",
            StringComparison.Ordinal);
        string invalidToken = valid.Replace(
            "932f13b0573f311f1b7a0e2b2376f4bfa086a9669088438cfd95974f98135c2b",
            "invoice.pdf",
            StringComparison.Ordinal);
        string nonUtc = valid.Replace(
            "2026-07-16T10:30:00Z",
            "2026-07-16T18:30:00+08:00",
            StringComparison.Ordinal);

        ContractParseResult<ResearchEventContract> raw =
            ContractJson.ParseResearchEvent(Encoding.UTF8.GetBytes(rawPath));
        ContractParseResult<ResearchEventContract> token =
            ContractJson.ParseResearchEvent(Encoding.UTF8.GetBytes(invalidToken));
        ContractParseResult<ResearchEventContract> time =
            ContractJson.ParseResearchEvent(Encoding.UTF8.GetBytes(nonUtc));

        Assert.Equal("contract.invalid_json", raw.Error?.Code);
        Assert.Equal("contract.invalid_research_event", token.Error?.Code);
        Assert.Equal("contract.invalid_research_event", time.Error?.Code);
    }

    [Theory]
    [InlineData("clarification_completed", ResearchEventType.ClarificationCompleted)]
    [InlineData("approval_decided", ResearchEventType.ApprovalDecided)]
    public void ResearchEventParserAcceptsClosedStudyTimingDiscriminators(
        string discriminator,
        ResearchEventType expected)
    {
        string json = ReadText("research-event.example.json").Replace(
            "rehearsal_completed",
            discriminator,
            StringComparison.Ordinal);

        ContractParseResult<ResearchEventContract> result =
            ContractJson.ParseResearchEvent(Encoding.UTF8.GetBytes(json));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(expected, result.Value!.Type);
    }

    [Theory]
    [InlineData("\"hwnd\": \"0x000A102C\"", "\"hwnd\": \"not-a-handle\"")]
    [InlineData("\"processId\": 4242", "\"processId\": 0")]
    [InlineData(
        "\"processStartedAt\": \"2026-07-15T15:45:00Z\"",
        "\"processStartedAt\": \"2026-07-15T17:45:00+02:00\"")]
    [InlineData(
        "\"issuedAt\": \"2026-07-15T16:00:00Z\"",
        "\"issuedAt\": \"2026-07-15T18:00:00+02:00\"")]
    [InlineData(
        "\"expiresAt\": \"2026-07-15T16:30:00Z\"",
        "\"expiresAt\": \"2026-07-15T15:30:00Z\"")]
    public void WindowLeaseParserRejectsInvalidIdentityAndNonUtcLifecycleData(
        string original,
        string replacement)
    {
        string json = ReadText("scope-lease.example.json").Replace(
            original,
            replacement,
            StringComparison.Ordinal);

        ContractParseResult<WindowLeaseContract> result =
            ContractJson.ParseWindowLease(Encoding.UTF8.GetBytes(json));

        Assert.False(result.IsSuccess);
        Assert.Equal("contract.invalid_window_lease", result.Error?.Code);
    }

    [Fact]
    public void WindowLeaseParserRequiresRevocationToMatchTerminalState()
    {
        string json = ReadText("scope-lease.example.json").Replace(
            "\"state\": \"active\"",
            "\"state\": \"expired\"",
            StringComparison.Ordinal);

        ContractParseResult<WindowLeaseContract> result =
            ContractJson.ParseWindowLease(Encoding.UTF8.GetBytes(json));

        Assert.False(result.IsSuccess);
        Assert.Equal("contract.invalid_window_lease", result.Error?.Code);
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
