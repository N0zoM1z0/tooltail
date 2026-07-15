namespace Tooltail.Features.FileSkills.Observation;

public enum WatcherSourceSignalKind
{
    Created,
    Deleted,
    Changed,
    Renamed,
    Overflow,
    Fault,
}

public sealed record WatcherSourceSignal
{
    private WatcherSourceSignal(
        WatcherSourceSignalKind kind,
        string? fullPath,
        string? oldFullPath)
    {
        Kind = kind;
        FullPath = fullPath;
        OldFullPath = oldFullPath;
    }

    public WatcherSourceSignalKind Kind { get; }

    public string? FullPath { get; }

    public string? OldFullPath { get; }

    public static WatcherSourceSignal Path(WatcherSourceSignalKind kind, string fullPath)
    {
        if (kind is not (WatcherSourceSignalKind.Created or
            WatcherSourceSignalKind.Deleted or
            WatcherSourceSignalKind.Changed))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        return new WatcherSourceSignal(kind, fullPath, oldFullPath: null);
    }

    public static WatcherSourceSignal Renamed(string oldFullPath, string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldFullPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        return new WatcherSourceSignal(WatcherSourceSignalKind.Renamed, fullPath, oldFullPath);
    }

    public static WatcherSourceSignal Overflow() =>
        new(WatcherSourceSignalKind.Overflow, fullPath: null, oldFullPath: null);

    public static WatcherSourceSignal Fault() =>
        new(WatcherSourceSignalKind.Fault, fullPath: null, oldFullPath: null);
}

public interface IWatcherHintSource : IAsyncDisposable
{
    void Start();

    ValueTask<bool> StopAndQuiesceAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public interface IWatcherHintSourceFactory
{
    IWatcherHintSource Create(
        string canonicalRoot,
        Action<WatcherSourceSignal> signalSink,
        int internalBufferSize);
}

public sealed record WatcherHintLimits
{
    public WatcherHintLimits(
        int maximumHints,
        int internalBufferSize,
        TimeSpan quiescenceTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumHints, 1);
        if (internalBufferSize is < 4096 or > 65536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(internalBufferSize),
                "The watcher buffer must stay between 4 KiB and 64 KiB.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            quiescenceTimeout,
            TimeSpan.Zero);
        MaximumHints = maximumHints;
        InternalBufferSize = internalBufferSize;
        QuiescenceTimeout = quiescenceTimeout;
    }

    public int MaximumHints { get; }

    public int InternalBufferSize { get; }

    public TimeSpan QuiescenceTimeout { get; }

    public static WatcherHintLimits Default { get; } = new(
        maximumHints: 4096,
        internalBufferSize: 16 * 1024,
        quiescenceTimeout: TimeSpan.FromSeconds(2));
}
