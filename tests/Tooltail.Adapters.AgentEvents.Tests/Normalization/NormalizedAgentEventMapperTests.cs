using System.Text;
using Tooltail.Adapters.AgentEvents.Normalization;
using Tooltail.Contracts.AgentEvents;
using Tooltail.Contracts.Json;
using Tooltail.Domain.Agents;

namespace Tooltail.Adapters.AgentEvents.Tests.Normalization;

public sealed class NormalizedAgentEventMapperTests
{
    [Fact]
    public void BundledNormalizedTraceMapsAndProjectsDeterministically()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "examples",
            "agent-events.example.jsonl");
        AgentRunProjection? projection = null;
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            ContractParseResult<AgentEventContract> parsed =
                ContractJson.ParseAgentEvent(Encoding.UTF8.GetBytes(line));
            Assert.True(parsed.IsSuccess, parsed.Error?.ToString());
            var mapped = NormalizedAgentEventMapper.Map(parsed.Value!);
            Assert.True(mapped.IsSuccess, mapped.Error?.ToString());
            projection ??= AgentRunProjection.Empty(mapped.Value!.RunId);
            var applied = CompanionStateProjector.Apply(projection, mapped.Value!);
            Assert.True(applied.IsSuccess, applied.Error?.ToString());
            projection = applied.Value!.Projection;
        }

        Assert.NotNull(projection);
        Assert.Equal(6, projection.LastSequence);
        Assert.Equal(CompanionBodyState.Observing, projection.BodyState);
        Assert.Empty(projection.ActiveTools);
        Assert.Empty(projection.PendingQuestionIds);
    }

    [Fact]
    public void SemanticMapperRejectsMissingToolIdentityAfterJsonParsing()
    {
        string json = ExampleLine(2).Replace(
            "\"toolCallId\":\"file-plan-1\",",
            string.Empty,
            StringComparison.Ordinal);
        ContractParseResult<AgentEventContract> parsed =
            ContractJson.ParseAgentEvent(Encoding.UTF8.GetBytes(json));

        var result = NormalizedAgentEventMapper.Map(parsed.Value!);

        Assert.True(parsed.IsSuccess);
        Assert.False(result.IsSuccess);
        Assert.Equal("agent_event.required_data_missing", result.Error?.Code);
    }

    [Fact]
    public void SemanticMapperRejectsControlTextAndUnsafeStatusCode()
    {
        string controlJson = ExampleLine(0).Replace(
            "Organize fixture files",
            "unsafe\\u001b[31m",
            StringComparison.Ordinal);
        string statusJson = ExampleLine(1).Replace(
            "granted_context_only",
            "NOT SAFE",
            StringComparison.Ordinal);
        AgentEventContract controlContract =
            ContractJson.ParseAgentEvent(Encoding.UTF8.GetBytes(controlJson)).Value!;
        AgentEventContract statusContract =
            ContractJson.ParseAgentEvent(Encoding.UTF8.GetBytes(statusJson)).Value!;

        var control = NormalizedAgentEventMapper.Map(controlContract);
        var status = NormalizedAgentEventMapper.Map(statusContract);

        Assert.Equal("agent_event.display_label_invalid", control.Error?.Code);
        Assert.Equal("agent_event.status_code_invalid", status.Error?.Code);
    }

    [Fact]
    public void SemanticMapperRejectsEmptyIdentityAndUnknownEnum()
    {
        AgentEventContract contract = ContractJson
            .ParseAgentEvent(Encoding.UTF8.GetBytes(ExampleLine(0)))
            .Value!;

        var emptyIdentity = NormalizedAgentEventMapper.Map(
            contract with { EventId = Guid.Empty });
        var unknownType = NormalizedAgentEventMapper.Map(
            contract with { Type = (AgentEventType)999 });

        Assert.Equal("agent_event.identity_empty", emptyIdentity.Error?.Code);
        Assert.Equal("agent_event.enum_unknown", unknownType.Error?.Code);
    }

    [Fact]
    public void SeparatelyParsedExactDuplicateIsIdempotent()
    {
        byte[] json = Encoding.UTF8.GetBytes(ExampleLine(0));
        NormalizedAgentEvent first = NormalizedAgentEventMapper.Map(
            ContractJson.ParseAgentEvent(json).Value!).Value!;
        NormalizedAgentEvent second = NormalizedAgentEventMapper.Map(
            ContractJson.ParseAgentEvent(json).Value!).Value!;
        AgentRunProjection projection = CompanionStateProjector.Apply(
            AgentRunProjection.Empty(first.RunId),
            first).Value!.Projection;

        var duplicate = CompanionStateProjector.Apply(projection, second);

        Assert.True(duplicate.IsSuccess);
        Assert.Equal(AgentEventDisposition.DuplicateIgnored, duplicate.Value!.Disposition);
        Assert.Same(projection, duplicate.Value.Projection);
    }

    private static string ExampleLine(int index) =>
        File.ReadLines(
                Path.Combine(
                    FindRepositoryRoot(),
                    "docs",
                    "examples",
                    "agent-events.example.jsonl"))
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ElementAt(index);

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

        throw new DirectoryNotFoundException("Could not locate Tooltail.sln from the adapter test output directory.");
    }
}
