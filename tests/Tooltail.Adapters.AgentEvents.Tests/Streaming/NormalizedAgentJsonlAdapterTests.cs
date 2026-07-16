using System.Text;
using Tooltail.Adapters.AgentEvents.Streaming;
using Tooltail.Contracts.AgentEvents;
using Tooltail.Contracts.Json;
using Tooltail.Domain.Agents;

namespace Tooltail.Adapters.AgentEvents.Tests.Streaming;

public sealed class NormalizedAgentJsonlAdapterTests
{
    private static readonly Guid RunId =
        Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

    [Fact]
    public async Task FragmentedCrLfStreamMapsAndProjectsDeterministically()
    {
        byte[] jsonl = JoinLines(
            "\r\n",
            Event(0, AgentEventType.RunStarted),
            Event(
                1,
                AgentEventType.ToolStarted,
                new AgentEventDataContract
                {
                    ToolKind = AgentToolKind.Code,
                    ToolCallId = "code-1",
                }),
            Event(
                2,
                AgentEventType.ToolCompleted,
                new AgentEventDataContract
                {
                    ToolKind = AgentToolKind.Code,
                    ToolCallId = "code-1",
                }),
            Event(3, AgentEventType.RunCompleted));
        await using Stream stream = new ChunkedReadStream(jsonl, 1, 2, 7, 3, 11);

        AgentEventStreamResult result = await NormalizedAgentJsonlAdapter.ReadAsync(
            stream,
            NormalizedAgentEventSource.Simulator);

        Assert.True(result.IsSuccess, result.ReasonCode);
        Assert.Equal("agent_stream.complete", result.ReasonCode);
        Assert.Equal(4, result.InputLineCount);
        Assert.Equal(jsonl.Length, result.InputByteCount);
        Assert.Equal(4, result.AcceptedEventCount);
        Assert.Equal(0, result.DuplicateEventCount);
        Assert.Equal(
            [
                CompanionBodyState.Working,
                CompanionBodyState.Working,
                CompanionBodyState.Working,
                CompanionBodyState.CompletedReceipt,
            ],
            result.Steps.Select(static step => step.Body.State));
        Assert.Equal(NormalizedAgentToolKind.Code, result.Steps[1].Body.ToolKind);
    }

    [Fact]
    public async Task ExactDuplicateIsCountedWithoutChangingProjection()
    {
        byte[] started = Event(0, AgentEventType.RunStarted);
        byte[] jsonl = JoinLines("\n", started, started);

        AgentEventStreamResult result = await NormalizedAgentJsonlAdapter.ReadAsync(
            new MemoryStream(jsonl, writable: false),
            NormalizedAgentEventSource.Simulator);

        Assert.True(result.IsSuccess, result.ReasonCode);
        Assert.Equal(2, result.AcceptedEventCount);
        Assert.Equal(1, result.DuplicateEventCount);
        Assert.Equal(AgentEventDisposition.DuplicateIgnored, result.Steps[1].Disposition);
        Assert.Equal(result.Steps[0].Body, result.Steps[1].Body);
    }

    [Theory]
    [InlineData("{not-json}\n", "contract.invalid_json")]
    [InlineData("\n\r\n", "agent_stream.empty")]
    public async Task InvalidOrEmptyInputIsRejected(string input, string reasonCode)
    {
        AgentEventStreamResult result = await NormalizedAgentJsonlAdapter.ReadAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(input), writable: false),
            NormalizedAgentEventSource.Simulator);

        Assert.Equal(AgentEventStreamStatus.Rejected, result.Status);
        Assert.Equal(reasonCode, result.ReasonCode);
    }

    [Fact]
    public async Task InvalidUtf8IsRejectedAsInvalidJson()
    {
        AgentEventStreamResult result = await NormalizedAgentJsonlAdapter.ReadAsync(
            new MemoryStream([0xff, (byte)'\n'], writable: false),
            NormalizedAgentEventSource.Simulator);

        Assert.Equal(AgentEventStreamStatus.Rejected, result.Status);
        Assert.Equal("contract.invalid_json", result.ReasonCode);
    }

    [Fact]
    public async Task SourceMismatchIsRejectedBeforeProjection()
    {
        byte[] input = JoinLines(
            "\n",
            Event(
                0,
                AgentEventType.RunStarted,
                source: AgentEventSource.GenericJsonl));

        AgentEventStreamResult result = await NormalizedAgentJsonlAdapter.ReadAsync(
            new MemoryStream(input, writable: false),
            NormalizedAgentEventSource.Simulator);

        Assert.Equal(AgentEventStreamStatus.Rejected, result.Status);
        Assert.Equal("agent_stream.source_mismatch", result.ReasonCode);
        Assert.Null(result.FinalProjection);
    }

    [Fact]
    public async Task GenericNormalizedSourceUsesTheSameStrictProjectionBoundary()
    {
        byte[] input = JoinLines(
            "\n",
            Event(
                0,
                AgentEventType.RunStarted,
                source: AgentEventSource.GenericJsonl),
            Event(
                1,
                AgentEventType.RunCompleted,
                source: AgentEventSource.GenericJsonl));

        AgentEventStreamResult result = await NormalizedAgentJsonlAdapter.ReadAsync(
            new MemoryStream(input, writable: false),
            NormalizedAgentEventSource.GenericJsonl);

        Assert.True(result.IsSuccess, result.ReasonCode);
        Assert.Equal(CompanionBodyState.CompletedReceipt, result.FinalProjection!.BodyState);
    }

    [Fact]
    public async Task SecondRunIsRejectedWithoutReplacingFirstProjection()
    {
        byte[] input = JoinLines(
            "\n",
            Event(0, AgentEventType.RunStarted),
            Event(
                1,
                AgentEventType.Heartbeat,
                runId: Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")));

        AgentEventStreamResult result = await NormalizedAgentJsonlAdapter.ReadAsync(
            new MemoryStream(input, writable: false),
            NormalizedAgentEventSource.Simulator);

        Assert.Equal(AgentEventStreamStatus.Rejected, result.Status);
        Assert.Equal("agent_stream.run_mismatch", result.ReasonCode);
        Assert.Equal(1, result.AcceptedEventCount);
        Assert.Equal(RunId, result.RunId?.Value);
    }

    [Fact]
    public async Task EventCountLimitStopsBeforeApplyingNextEvent()
    {
        byte[] input = JoinLines(
            "\n",
            Event(0, AgentEventType.RunStarted),
            Event(1, AgentEventType.Heartbeat));
        var limits = new AgentEventStreamLimits(
            maximumLineBytes: 1024,
            maximumTotalBytes: 2048,
            maximumEvents: 1,
            readBufferBytes: 256);

        AgentEventStreamResult result = await NormalizedAgentJsonlAdapter.ReadAsync(
            new MemoryStream(input, writable: false),
            NormalizedAgentEventSource.Simulator,
            limits);

        Assert.Equal(AgentEventStreamStatus.Rejected, result.Status);
        Assert.Equal("agent_stream.event_limit", result.ReasonCode);
        Assert.Equal(1, result.AcceptedEventCount);
        Assert.Single(result.Steps);
    }

    [Fact]
    public async Task OversizedLineIsRejectedBeforeJsonParsing()
    {
        byte[] input = Encoding.UTF8.GetBytes(new string('x', 300));
        var limits = new AgentEventStreamLimits(
            maximumLineBytes: 256,
            maximumTotalBytes: 512,
            maximumEvents: 4,
            readBufferBytes: 256);

        AgentEventStreamResult result = await NormalizedAgentJsonlAdapter.ReadAsync(
            new MemoryStream(input, writable: false),
            NormalizedAgentEventSource.Simulator,
            limits);

        Assert.Equal(AgentEventStreamStatus.Rejected, result.Status);
        Assert.Equal("agent_stream.line_byte_limit", result.ReasonCode);
        Assert.Equal(1, result.InputLineCount);
    }

    [Fact]
    public async Task TotalByteLimitRejectsEvenEmptyLines()
    {
        byte[] input = Enumerable.Repeat((byte)'\n', 300).ToArray();
        var limits = new AgentEventStreamLimits(
            maximumLineBytes: 256,
            maximumTotalBytes: 256,
            maximumEvents: 4,
            readBufferBytes: 256);

        AgentEventStreamResult result = await NormalizedAgentJsonlAdapter.ReadAsync(
            new MemoryStream(input, writable: false),
            NormalizedAgentEventSource.Simulator,
            limits);

        Assert.Equal(AgentEventStreamStatus.Rejected, result.Status);
        Assert.Equal("agent_stream.total_byte_limit", result.ReasonCode);
        Assert.True(result.InputByteCount > 256);
    }

    [Fact]
    public async Task PreCancelledReadReturnsCancelledWithoutThrowing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        AgentEventStreamResult result = await NormalizedAgentJsonlAdapter.ReadAsync(
            new MemoryStream(Event(0, AgentEventType.RunStarted), writable: false),
            NormalizedAgentEventSource.Simulator,
            cancellationToken: cancellation.Token);

        Assert.Equal(AgentEventStreamStatus.Cancelled, result.Status);
        Assert.Equal("agent_stream.cancelled", result.ReasonCode);
        Assert.Empty(result.Steps);
    }

    [Fact]
    public async Task ReadFailureReturnsRedactedIoReason()
    {
        await using var input = new ThrowingReadStream();

        AgentEventStreamResult result = await NormalizedAgentJsonlAdapter.ReadAsync(
            input,
            NormalizedAgentEventSource.Simulator);

        Assert.Equal(AgentEventStreamStatus.IoFailure, result.Status);
        Assert.Equal("agent_stream.io_failure", result.ReasonCode);
        Assert.Empty(result.Steps);
    }

    private static byte[] Event(
        long sequence,
        AgentEventType type,
        AgentEventDataContract? data = null,
        AgentEventSource source = AgentEventSource.Simulator,
        Guid? runId = null) =>
        ContractJson.Serialize(
            new AgentEventContract
            {
                SchemaVersion = ContractVersions.V1,
                EventId = Guid.Parse($"00000000-0000-4000-8000-{sequence + 1:D12}"),
                RunId = runId ?? RunId,
                Sequence = sequence,
                OccurredAt = new DateTimeOffset(2026, 7, 16, 4, 0, 0, TimeSpan.Zero)
                    .AddSeconds(sequence),
                Source = source,
                Type = type,
                Severity = AgentEventSeverity.Info,
                Data = data ?? new AgentEventDataContract(),
            });

    private static byte[] JoinLines(string separator, params byte[][] lines) =>
        Encoding.UTF8.GetBytes(
            string.Join(
                separator,
                lines.Select(static line => Encoding.UTF8.GetString(line))) + separator);

    private sealed class ChunkedReadStream(byte[] content, params int[] chunkSizes) : Stream
    {
        private int position;
        private int chunkIndex;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => content.Length;

        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (position >= content.Length)
            {
                return ValueTask.FromResult(0);
            }

            int requestedChunk = chunkSizes[chunkIndex++ % chunkSizes.Length];
            int count = Math.Min(
                Math.Min(requestedChunk, buffer.Length),
                content.Length - position);
            content.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return ValueTask.FromResult(count);
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingReadStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("sensitive provider detail"));

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
