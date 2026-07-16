using System.Text;
using Tooltail.Adapters.AgentEvents.Codex;
using Tooltail.Adapters.AgentEvents.Streaming;
using Tooltail.Contracts.Json;
using Tooltail.Domain.Agents;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Adapters.AgentEvents.Tests.Codex;

public sealed class CodexJsonlAdapterTests
{
    private static readonly RunId RunId =
        new(Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"));

    [Fact]
    public async Task RedactedDocumentedFixtureProjectsWithoutRetainingRawContent()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "Tooltail.Adapters.AgentEvents.Tests",
            "Fixtures",
            "codex-exec-known.redacted.jsonl");
        await using FileStream input = File.OpenRead(path);

        CodexEventStreamResult result = await CodexJsonlAdapter.ReadAsync(input, RunId);

        Assert.True(result.IsSuccess, result.ReasonCode);
        Assert.Equal("agent_stream.complete", result.ReasonCode);
        Assert.Equal(7, result.ProcessedRawEventCount);
        Assert.Equal(4, result.EmittedEventCount);
        Assert.Equal(2, result.IgnoredKnownEventCount);
        Assert.Equal(1, result.IgnoredUnknownEventCount);
        Assert.Equal(
            [
                CompanionBodyState.Working,
                CompanionBodyState.Working,
                CompanionBodyState.Working,
                CompanionBodyState.CompletedReceipt,
            ],
            result.Steps.Select(static step => step.Body.State));
        Assert.Equal(NormalizedAgentToolKind.Terminal, result.Steps[1].Body.ToolKind);

        string safeResult = Encoding.UTF8.GetString(ContractJson.Serialize(result));
        Assert.DoesNotContain("provider-command-1", safeResult, StringComparison.Ordinal);
        Assert.DoesNotContain("command", safeResult, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("aggregated_output", safeResult, StringComparison.Ordinal);
        Assert.DoesNotContain("<redacted>", safeResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedLineTerminatesAsVisibleDisconnect()
    {
        byte[] input = Jsonl(
            "{\"type\":\"thread.started\"}",
            "{\"type\":");

        CodexEventStreamResult result = await CodexJsonlAdapter.ReadAsync(
            new MemoryStream(input, writable: false),
            RunId);

        Assert.Equal(AgentEventStreamStatus.Rejected, result.Status);
        Assert.Equal("codex_adapter.invalid_json", result.ReasonCode);
        Assert.Equal(2, result.EmittedEventCount);
        Assert.Equal(CompanionBodyState.Disconnected, result.FinalProjection.BodyState);
        Assert.True(result.Steps[^1].AdapterSynthesized);
        Assert.Null(result.Steps[^1].InputLine);
    }

    [Fact]
    public async Task ProviderErrorIsVisibleAndDoesNotExposePayload()
    {
        const string secret = "TOKEN-DO-NOT-RETAIN";
        byte[] input = Jsonl(
            "{\"type\":\"thread.started\"}",
            $"{{\"type\":\"error\",\"message\":\"{secret}\"}}");

        CodexEventStreamResult result = await CodexJsonlAdapter.ReadAsync(
            new MemoryStream(input, writable: false),
            RunId);

        Assert.Equal(AgentEventStreamStatus.Rejected, result.Status);
        Assert.Equal("codex_adapter.provider_error", result.ReasonCode);
        Assert.Equal(CompanionBodyState.Disconnected, result.FinalProjection.BodyState);
        Assert.DoesNotContain(
            secret,
            Encoding.UTF8.GetString(ContractJson.Serialize(result)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownOnlyStreamIsCountedThenFailsClosedAtEof()
    {
        byte[] input = Jsonl("{\"type\":\"provider.future\",\"payload\":\"ignored\"}");

        CodexEventStreamResult result = await CodexJsonlAdapter.ReadAsync(
            new MemoryStream(input, writable: false),
            RunId);

        Assert.Equal(AgentEventStreamStatus.Rejected, result.Status);
        Assert.Equal("codex_adapter.unexpected_eof", result.ReasonCode);
        Assert.Equal(1, result.IgnoredUnknownEventCount);
        Assert.Equal(CompanionBodyState.Disconnected, result.FinalProjection.BodyState);
    }

    [Fact]
    public async Task RawEventLimitCountsIgnoredEventsAndFailsClosed()
    {
        byte[] input = Jsonl(
            "{\"type\":\"provider.future.one\"}",
            "{\"type\":\"provider.future.two\"}");
        var limits = new AgentEventStreamLimits(
            maximumLineBytes: 1024,
            maximumTotalBytes: 2048,
            maximumEvents: 1,
            readBufferBytes: 256);

        CodexEventStreamResult result = await CodexJsonlAdapter.ReadAsync(
            new MemoryStream(input, writable: false),
            RunId,
            limits);

        Assert.Equal(AgentEventStreamStatus.Rejected, result.Status);
        Assert.Equal("codex_adapter.event_limit", result.ReasonCode);
        Assert.Equal(1, result.ProcessedRawEventCount);
        Assert.Equal(1, result.IgnoredUnknownEventCount);
        Assert.Equal(CompanionBodyState.Disconnected, result.FinalProjection.BodyState);
    }

    [Fact]
    public async Task PreCancelledStreamReturnsBoundedCancelledProjection()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        CodexEventStreamResult result = await CodexJsonlAdapter.ReadAsync(
            new MemoryStream(Jsonl("{\"type\":\"thread.started\"}"), writable: false),
            RunId,
            cancellationToken: cancellation.Token);

        Assert.Equal(AgentEventStreamStatus.Cancelled, result.Status);
        Assert.Equal("agent_stream.cancelled", result.ReasonCode);
        Assert.Equal(CompanionBodyState.Disconnected, result.FinalProjection.BodyState);
        Assert.Equal("body.disconnected", result.FinalProjection.Body.ReasonCode);
    }

    [Fact]
    public async Task ToolFailureOutranksLaterProviderFailureSummary()
    {
        byte[] input = Jsonl(
            "{\"type\":\"thread.started\"}",
            "{\"type\":\"item.started\",\"item\":{\"id\":\"call-1\",\"type\":\"command_execution\"}}",
            "{\"type\":\"item.completed\",\"item\":{\"id\":\"call-1\",\"type\":\"command_execution\",\"status\":\"failed\",\"aggregated_output\":\"secret\"}}",
            "{\"type\":\"turn.failed\",\"error\":{\"message\":\"secret\"}}");

        CodexEventStreamResult result = await CodexJsonlAdapter.ReadAsync(
            new MemoryStream(input, writable: false),
            RunId);

        Assert.True(result.IsSuccess, result.ReasonCode);
        Assert.Equal(3, result.EmittedEventCount);
        Assert.Equal(1, result.IgnoredKnownEventCount);
        Assert.Equal(CompanionBodyState.Failed, result.FinalProjection.BodyState);
    }

    [Fact]
    public void RawItemIdentityIsHashedAndContentItemsAreDiscarded()
    {
        var normalizer = new CodexRawEventNormalizer(RunId);
        _ = normalizer.Map(Utf8("{\"type\":\"thread.started\"}"));

        CodexRawEventMapResult tool = normalizer.Map(
            Utf8(
                "{\"type\":\"item.started\",\"item\":{\"id\":\"sensitive-provider-id\",\"type\":\"web_search\",\"query\":\"sensitive query\"}}"));
        CodexRawEventMapResult message = normalizer.Map(
            Utf8(
                "{\"type\":\"item.completed\",\"item\":{\"id\":\"message-1\",\"type\":\"agent_message\",\"text\":\"sensitive answer\"}}"));

        Assert.Equal(CodexRawEventDisposition.Emitted, tool.Disposition);
        Assert.StartsWith("codex-item-", tool.Event!.Data.ToolCallId, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "sensitive-provider-id",
            tool.Event.Data.ToolCallId,
            StringComparison.Ordinal);
        Assert.Equal(CodexRawEventDisposition.IgnoredKnown, message.Disposition);
        Assert.Null(message.Event);
    }

    private static ReadOnlyMemory<byte> Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static byte[] Jsonl(params string[] lines) =>
        Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");

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

        throw new DirectoryNotFoundException(
            "Could not locate Tooltail.sln from the adapter test output directory.");
    }
}
