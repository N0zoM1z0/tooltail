using System.Buffers;

namespace Tooltail.Adapters.AgentEvents.Streaming;

internal sealed record BoundedJsonlReadResult(
    AgentEventStreamStatus Status,
    string ReasonCode,
    int LineCount,
    long ByteCount);

internal static class BoundedJsonlLineReader
{
    public static async Task<BoundedJsonlReadResult> ReadAsync(
        Stream input,
        AgentEventStreamLimits limits,
        Func<ReadOnlyMemory<byte>, int, string?> handleLine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(handleLine);
        if (!input.CanRead)
        {
            return Failure(
                AgentEventStreamStatus.IoFailure,
                "agent_stream.not_readable");
        }

        byte[] readBuffer = ArrayPool<byte>.Shared.Rent(limits.ReadBufferBytes);
        byte[] lineBuffer = ArrayPool<byte>.Shared.Rent(limits.MaximumLineBytes + 1);
        int lineLength = 0;
        int lineCount = 0;
        long byteCount = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = await input.ReadAsync(
                    readBuffer.AsMemory(0, limits.ReadBufferBytes),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                byteCount = checked(byteCount + read);
                if (byteCount > limits.MaximumTotalBytes)
                {
                    return Failure(
                        AgentEventStreamStatus.Rejected,
                        "agent_stream.total_byte_limit",
                        lineCount,
                        byteCount);
                }

                for (int index = 0; index < read; index++)
                {
                    byte value = readBuffer[index];
                    if (value == (byte)'\n')
                    {
                        string? failure = ProcessLine(
                            lineBuffer,
                            lineLength,
                            ref lineCount,
                            handleLine);
                        if (failure is not null)
                        {
                            return Failure(
                                AgentEventStreamStatus.Rejected,
                                failure,
                                lineCount,
                                byteCount);
                        }

                        lineLength = 0;
                        continue;
                    }

                    if (lineLength >= limits.MaximumLineBytes)
                    {
                        return Failure(
                            AgentEventStreamStatus.Rejected,
                            "agent_stream.line_byte_limit",
                            lineCount + 1,
                            byteCount);
                    }

                    lineBuffer[lineLength++] = value;
                }
            }

            if (lineLength > 0)
            {
                string? failure = ProcessLine(
                    lineBuffer,
                    lineLength,
                    ref lineCount,
                    handleLine);
                if (failure is not null)
                {
                    return Failure(
                        AgentEventStreamStatus.Rejected,
                        failure,
                        lineCount,
                        byteCount);
                }
            }

            return new BoundedJsonlReadResult(
                AgentEventStreamStatus.Completed,
                "agent_stream.complete",
                lineCount,
                byteCount);
        }
        catch (OperationCanceledException)
        {
            return Failure(
                AgentEventStreamStatus.Cancelled,
                "agent_stream.cancelled",
                lineCount,
                byteCount);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return Failure(
                AgentEventStreamStatus.IoFailure,
                "agent_stream.io_failure",
                lineCount,
                byteCount);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer, clearArray: true);
            ArrayPool<byte>.Shared.Return(lineBuffer, clearArray: true);
        }
    }

    private static string? ProcessLine(
        byte[] buffer,
        int length,
        ref int lineCount,
        Func<ReadOnlyMemory<byte>, int, string?> handleLine)
    {
        lineCount++;
        if (length > 0 && buffer[length - 1] == (byte)'\r')
        {
            length--;
        }

        if (length == 0)
        {
            return null;
        }

        return handleLine(buffer.AsMemory(0, length), lineCount);
    }

    private static BoundedJsonlReadResult Failure(
        AgentEventStreamStatus status,
        string reasonCode,
        int lineCount = 0,
        long byteCount = 0) => new(
            status,
            reasonCode,
            lineCount,
            byteCount);
}
