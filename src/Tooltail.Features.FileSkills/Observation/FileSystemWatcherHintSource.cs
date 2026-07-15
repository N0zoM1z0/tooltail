namespace Tooltail.Features.FileSkills.Observation;

public sealed class FileSystemWatcherHintSourceFactory : IWatcherHintSourceFactory
{
    public IWatcherHintSource Create(
        string canonicalRoot,
        Action<WatcherSourceSignal> signalSink,
        int internalBufferSize) =>
        new FileSystemWatcherHintSource(canonicalRoot, signalSink, internalBufferSize);
}

internal sealed class FileSystemWatcherHintSource : IWatcherHintSource
{
    private readonly FileSystemWatcher watcher;
    private readonly Action<WatcherSourceSignal> signalSink;
    private readonly TaskCompletionSource<bool> callbacksDrained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int activeCallbacks;
    private int started;
    private int stopping;
    private int disposed;

    public FileSystemWatcherHintSource(
        string canonicalRoot,
        Action<WatcherSourceSignal> signalSink,
        int internalBufferSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);
        ArgumentNullException.ThrowIfNull(signalSink);
        if (internalBufferSize is < 4096 or > 65536)
        {
            throw new ArgumentOutOfRangeException(nameof(internalBufferSize));
        }

        this.signalSink = signalSink;
        FileSystemWatcher created = new(canonicalRoot)
        {
            Filter = "*",
            IncludeSubdirectories = true,
            InternalBufferSize = internalBufferSize,
            NotifyFilter = NotifyFilters.FileName |
                NotifyFilters.DirectoryName |
                NotifyFilters.Attributes |
                NotifyFilters.Size |
                NotifyFilters.LastWrite |
                NotifyFilters.CreationTime,
        };
        try
        {
            created.Created += OnCreated;
            created.Deleted += OnDeleted;
            created.Changed += OnChanged;
            created.Renamed += OnRenamed;
            created.Error += OnError;
            watcher = created;
        }
        catch
        {
            created.Dispose();
            throw;
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.CompareExchange(ref started, 1, 0) != 0)
        {
            throw new InvalidOperationException("A watcher hint source can start only once.");
        }

        watcher.EnableRaisingEvents = true;
    }

    public async ValueTask<bool> StopAndQuiesceAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        if (Interlocked.Exchange(ref stopping, 1) == 0 &&
            Volatile.Read(ref disposed) == 0)
        {
            watcher.EnableRaisingEvents = false;
        }

        if (Volatile.Read(ref activeCallbacks) == 0)
        {
            return true;
        }

        Task delay = Task.Delay(timeout, cancellationToken);
        Task completed = await Task.WhenAny(callbacksDrained.Task, delay).ConfigureAwait(false);
        if (completed == callbacksDrained.Task)
        {
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        Interlocked.Exchange(ref stopping, 1);
        watcher.EnableRaisingEvents = false;
        watcher.Created -= OnCreated;
        watcher.Deleted -= OnDeleted;
        watcher.Changed -= OnChanged;
        watcher.Renamed -= OnRenamed;
        watcher.Error -= OnError;
        watcher.Dispose();
        return ValueTask.CompletedTask;
    }

    private void OnCreated(object sender, FileSystemEventArgs eventArgs) =>
        Dispatch(
            WatcherSourceSignal.Path(
                WatcherSourceSignalKind.Created,
                eventArgs.FullPath));

    private void OnDeleted(object sender, FileSystemEventArgs eventArgs) =>
        Dispatch(
            WatcherSourceSignal.Path(
                WatcherSourceSignalKind.Deleted,
                eventArgs.FullPath));

    private void OnChanged(object sender, FileSystemEventArgs eventArgs) =>
        Dispatch(
            WatcherSourceSignal.Path(
                WatcherSourceSignalKind.Changed,
                eventArgs.FullPath));

    private void OnRenamed(object sender, RenamedEventArgs eventArgs) =>
        Dispatch(WatcherSourceSignal.Renamed(eventArgs.OldFullPath, eventArgs.FullPath));

    private void OnError(object sender, ErrorEventArgs eventArgs) =>
        Dispatch(
            eventArgs.GetException() is InternalBufferOverflowException
                ? WatcherSourceSignal.Overflow()
                : WatcherSourceSignal.Fault());

    private void Dispatch(WatcherSourceSignal signal)
    {
        Interlocked.Increment(ref activeCallbacks);
        try
        {
            if (Volatile.Read(ref stopping) == 0 && Volatile.Read(ref disposed) == 0)
            {
                try
                {
                    signalSink(signal);
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    try
                    {
                        signalSink(WatcherSourceSignal.Fault());
                    }
                    catch (Exception nested) when (!IsFatal(nested))
                    {
                    }
                }
            }
        }
        finally
        {
            if (Interlocked.Decrement(ref activeCallbacks) == 0 &&
                Volatile.Read(ref stopping) != 0)
            {
                callbacksDrained.TrySetResult(true);
            }
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or
            StackOverflowException or
            AccessViolationException;
}
