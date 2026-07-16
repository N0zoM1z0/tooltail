using Tooltail.Application.Windows;
using Tooltail.Platform.Windows.Windowing;

namespace Tooltail.Platform.Windows.Tests.Windowing;

public sealed class BoundedWindowSignalBufferTests
{
    [Fact]
    public async Task RepeatedCallbackBurstsCollapseToTheLatestSignalOfEachKind()
    {
        using BoundedWindowSignalBuffer buffer = new();
        for (ulong handle = 1; handle <= 1000; handle++)
        {
            buffer.Enqueue(new WindowTargetSignal(
                WindowTargetSignalKind.LocationChanged,
                handle));
        }

        Assert.Equal(1, buffer.PendingCount);
        await using IAsyncEnumerator<WindowTargetSignal> reader =
            buffer.ReadAllAsync().GetAsyncEnumerator();

        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(WindowTargetSignalKind.LocationChanged, reader.Current.Kind);
        Assert.Equal(1000UL, reader.Current.WindowHandle);
    }

    [Fact]
    public async Task TerminalSignalCannotBeDisplacedByLaterCallbackNoise()
    {
        using BoundedWindowSignalBuffer buffer = new();
        buffer.Enqueue(new WindowTargetSignal(WindowTargetSignalKind.LocationChanged, 0x10));
        buffer.Enqueue(new WindowTargetSignal(WindowTargetSignalKind.Destroyed, 0x10));
        Parallel.For(
            0,
            1000,
            _ => buffer.Enqueue(new WindowTargetSignal(
                WindowTargetSignalKind.LocationChanged,
                0x20)));

        List<WindowTargetSignal> received = [];
        await foreach (WindowTargetSignal signal in buffer.ReadAllAsync())
        {
            received.Add(signal);
        }

        WindowTargetSignal terminal = Assert.Single(received);
        Assert.Equal(WindowTargetSignalKind.Destroyed, terminal.Kind);
        Assert.Equal(0x10UL, terminal.WindowHandle);
    }

    [Fact]
    public void ConcurrentProducersRemainStrictlyBounded()
    {
        using BoundedWindowSignalBuffer buffer = new();

        Parallel.For(
            0,
            10000,
            index => buffer.Enqueue(new WindowTargetSignal(
                (WindowTargetSignalKind)(index %
                    Enum.GetValues<WindowTargetSignalKind>().Length),
                checked((ulong)index + 1))));

        Assert.InRange(
            buffer.PendingCount,
            0,
            BoundedWindowSignalBuffer.MaximumPendingSignals);
    }
}
