using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Tooltail.Application.Windows;

namespace Tooltail.Platform.Windows.Windowing;

internal sealed class BoundedWindowSignalBuffer : IDisposable
{
    internal const int MaximumPendingSignals = 16;

    private readonly object sync = new();
    private readonly List<WindowTargetSignal> pending = new(MaximumPendingSignals);
    private readonly Channel<byte> wake = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
    private bool terminalQueued;
    private bool disposed;

    internal int PendingCount
    {
        get
        {
            lock (sync)
            {
                return pending.Count;
            }
        }
    }

    internal void Enqueue(WindowTargetSignal signal)
    {
        bool wakeReader;
        lock (sync)
        {
            if (disposed || terminalQueued)
            {
                return;
            }

            if (IsTerminal(signal.Kind))
            {
                pending.Clear();
                pending.Add(signal);
                terminalQueued = true;
            }
            else
            {
                pending.RemoveAll(existing => existing.Kind == signal.Kind);
                if (pending.Count == MaximumPendingSignals)
                {
                    pending.RemoveAt(0);
                }

                pending.Add(signal);
            }

            wakeReader = pending.Count > 0;
        }

        if (wakeReader)
        {
            wake.Writer.TryWrite(0);
        }
    }

    internal async IAsyncEnumerable<WindowTargetSignal> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await wake.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (wake.Reader.TryRead(out _))
            {
            }

            WindowTargetSignal[] batch;
            lock (sync)
            {
                batch = pending.ToArray();
                pending.Clear();
            }

            foreach (WindowTargetSignal signal in batch)
            {
                yield return signal;
                if (IsTerminal(signal.Kind))
                {
                    yield break;
                }
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            pending.Clear();
        }

        wake.Writer.TryComplete();
    }

    private static bool IsTerminal(WindowTargetSignalKind kind) =>
        kind is WindowTargetSignalKind.Destroyed or WindowTargetSignalKind.ProcessExited;
}
