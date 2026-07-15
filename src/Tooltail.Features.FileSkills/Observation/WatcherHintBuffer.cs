using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Tooltail.Features.FileSkills.Paths;

namespace Tooltail.Features.FileSkills.Observation;

internal sealed class WatcherHintBuffer
{
    private readonly string canonicalRoot;
    private readonly int maximumHints;
    private readonly ConcurrentQueue<WatcherSourceSignal> signals = new();
    private int acceptedCount;
    private int droppedCount;
    private int overflowed;
    private int sourceFaulted;
    private int sealedForWrites;

    public WatcherHintBuffer(string canonicalRoot, int maximumHints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumHints, 1);
        this.canonicalRoot = Path.GetFullPath(canonicalRoot);
        this.maximumHints = maximumHints;
    }

    public bool IsInvalidated =>
        Volatile.Read(ref overflowed) != 0 ||
        Volatile.Read(ref sourceFaulted) != 0;

    public int AcceptedHintCount => Volatile.Read(ref acceptedCount);

    public void Record(WatcherSourceSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (Volatile.Read(ref sealedForWrites) != 0)
        {
            Interlocked.Increment(ref droppedCount);
            return;
        }

        if (signal.Kind == WatcherSourceSignalKind.Overflow)
        {
            Interlocked.Exchange(ref overflowed, 1);
            return;
        }

        if (signal.Kind == WatcherSourceSignalKind.Fault)
        {
            Interlocked.Exchange(ref sourceFaulted, 1);
            return;
        }

        if (Volatile.Read(ref overflowed) != 0 || Volatile.Read(ref sourceFaulted) != 0)
        {
            Interlocked.Increment(ref droppedCount);
            return;
        }

        int reserved = Interlocked.Increment(ref acceptedCount);
        if (reserved > maximumHints)
        {
            Interlocked.Decrement(ref acceptedCount);
            Interlocked.Increment(ref droppedCount);
            Interlocked.Exchange(ref overflowed, 1);
            return;
        }

        signals.Enqueue(signal);
    }

    public WatcherHintBatch SealAndDrain(bool quiesced)
    {
        Interlocked.Exchange(ref sealedForWrites, 1);
        List<WatcherHint> hints = [];
        bool normalizationFailed = false;
        while (signals.TryDequeue(out WatcherSourceSignal? signal))
        {
            if (!TryNormalize(signal, out WatcherHint? hint))
            {
                normalizationFailed = true;
                continue;
            }

            hints.Add(hint);
        }

        return new WatcherHintBatch(
            hints,
            Volatile.Read(ref overflowed) != 0,
            quiesced,
            Volatile.Read(ref droppedCount),
            sourceFaulted: normalizationFailed || Volatile.Read(ref sourceFaulted) != 0);
    }

    private bool TryNormalize(
        WatcherSourceSignal signal,
        [NotNullWhen(true)] out WatcherHint? hint)
    {
        hint = null;
        if (!TryRelative(signal.FullPath, out string? relative))
        {
            return false;
        }

        try
        {
            hint = signal.Kind switch
            {
                WatcherSourceSignalKind.Created =>
                    new WatcherHint(WatcherHintKind.Created, relative),
                WatcherSourceSignalKind.Deleted =>
                    new WatcherHint(WatcherHintKind.Deleted, relative),
                WatcherSourceSignalKind.Changed =>
                    new WatcherHint(WatcherHintKind.Changed, relative),
                WatcherSourceSignalKind.Renamed when
                    TryRelative(signal.OldFullPath, out string? oldRelative) =>
                    new WatcherHint(WatcherHintKind.Renamed, relative, oldRelative),
                _ => null,
            };
            return hint is not null;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool TryRelative(
        string? fullPath,
        [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        try
        {
            string relative = Path.GetRelativePath(canonicalRoot, Path.GetFullPath(fullPath));
            if (Path.DirectorySeparatorChar != '\\' && relative.Contains('\\'))
            {
                return false;
            }

            relative = relative
                .Replace(Path.DirectorySeparatorChar, '\\')
                .Replace(Path.AltDirectorySeparatorChar, '\\');
            PathSafetyResult<WindowsRelativePath> parsed = WindowsPathPolicy.ParseRelative(relative);
            if (!parsed.IsSuccess)
            {
                return false;
            }

            normalized = parsed.Value!.Value;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}
