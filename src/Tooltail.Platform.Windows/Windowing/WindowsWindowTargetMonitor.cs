using System.Runtime.InteropServices;
using Tooltail.Application.Abstractions;
using Tooltail.Application.Windows;
using Tooltail.Domain.Windows;

namespace Tooltail.Platform.Windows.Windowing;

internal sealed class WindowsWindowTargetMonitor : IWindowTargetMonitor
{
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromSeconds(1);

    private readonly WindowTargetIdentity target;
    private readonly bool skipOwnProcessEvents;
    private readonly BoundedWindowSignalBuffer signals = new();
    private readonly NativeWindowInterop.WinEventCallback callback;
    private readonly List<SafeWinEventHookHandle> hooks = [];
    private readonly ManualResetEventSlim hookThreadStarted = new(initialState: false);
    private readonly CancellationTokenSource startupAbort = new();
    private readonly TaskCompletionSource hookThreadCompleted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread hookThread;
    private readonly Timer reconciliationTimer;
    private Exception? hookThreadStartException;
    private uint hookThreadId;
    private int disposed;

    internal WindowsWindowTargetMonitor(
        WindowTargetIdentity target,
        bool skipOwnProcessEvents = true)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Native window monitoring requires Windows.");
        }

        this.target = target;
        this.skipOwnProcessEvents = skipOwnProcessEvents;
        callback = OnWinEvent;
        hookThread = new Thread(RunHookThread)
        {
            IsBackground = true,
            Name = "Tooltail bounded window event monitor",
        };
        hookThread.Start();
        if (!hookThreadStarted.Wait(TimeSpan.FromSeconds(10)))
        {
            startupAbort.Cancel();
            RequestHookThreadExit();
            throw new InvalidOperationException(
                "The bounded native window event monitor did not start in time.");
        }

        if (hookThreadStartException is not null)
        {
            hookThreadCompleted.Task.GetAwaiter().GetResult();
            signals.Dispose();
            startupAbort.Dispose();
            hookThreadStarted.Dispose();
            throw new InvalidOperationException(
                "The bounded native window event monitor could not register its hooks.",
                hookThreadStartException);
        }

        reconciliationTimer = new Timer(
            static state => ((WindowsWindowTargetMonitor)state!).RequestReconciliation(),
            this,
            ReconciliationInterval,
            ReconciliationInterval);
    }

    public IAsyncEnumerable<WindowTargetSignal> ReadAllAsync(
        CancellationToken cancellationToken = default) =>
        signals.ReadAllAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await reconciliationTimer.DisposeAsync().ConfigureAwait(false);
        RequestHookThreadExit();
        await hookThreadCompleted.Task.ConfigureAwait(false);
        signals.Dispose();
        startupAbort.Dispose();
        hookThreadStarted.Dispose();
        GC.KeepAlive(callback);
    }

    private void RunHookThread()
    {
        bool startedSuccessfully = false;
        try
        {
            hookThreadId = NativeWindowInterop.GetCurrentThreadId();
            NativeWindowInterop.PeekMessage(
                out _,
                nint.Zero,
                0,
                0,
                NativeWindowInterop.PeekMessageNoRemove);
            RegisterTargetHook(NativeWindowInterop.EventObjectDestroy);
            RegisterTargetHook(NativeWindowInterop.EventObjectLocationChange);
            RegisterTargetHook(NativeWindowInterop.EventObjectCloaked);
            RegisterTargetHook(NativeWindowInterop.EventObjectUncloaked);
            RegisterTargetHook(NativeWindowInterop.EventSystemMinimizeStart);
            RegisterTargetHook(NativeWindowInterop.EventSystemMinimizeEnd);
            RegisterHook(
                NativeWindowInterop.EventSystemForeground,
                NativeWindowInterop.EventSystemForeground,
                processId: 0);
            startedSuccessfully = true;
        }
        catch (Exception exception) when (exception is InvalidOperationException)
        {
            hookThreadStartException = exception;
        }
        finally
        {
            hookThreadStarted.Set();
        }

        try
        {
            if (startedSuccessfully && !startupAbort.IsCancellationRequested)
            {
                while (true)
                {
                    int result = NativeWindowInterop.GetMessage(
                        out NativeWindowInterop.NativeMessage message,
                        nint.Zero,
                        0,
                        0);
                    if (result <= 0)
                    {
                        break;
                    }

                    NativeWindowInterop.TranslateMessage(in message);
                    NativeWindowInterop.DispatchMessage(in message);
                }
            }
        }
        finally
        {
            DisposeHooks();
            if (startedSuccessfully && Volatile.Read(ref disposed) == 0)
            {
                signals.Dispose();
            }

            hookThreadCompleted.TrySetResult();
            if (startupAbort.IsCancellationRequested)
            {
                startupAbort.Dispose();
            }
        }
    }

    private void RegisterTargetHook(uint eventType) =>
        RegisterHook(eventType, eventType, checked((uint)target.ProcessId));

    private void RegisterHook(uint eventMinimum, uint eventMaximum, uint processId)
    {
        nint callbackPointer = Marshal.GetFunctionPointerForDelegate(callback);
        nint hook = NativeWindowInterop.SetWinEventHook(
            eventMinimum,
            eventMaximum,
            eventHookModule: nint.Zero,
            callbackPointer,
            processId,
            threadId: 0,
            NativeWindowInterop.WinEventOutOfContext |
            (skipOwnProcessEvents ? NativeWindowInterop.WinEventSkipOwnProcess : 0));
        if (hook == nint.Zero)
        {
            throw new InvalidOperationException("A bounded native window event hook could not be registered.");
        }

        hooks.Add(new SafeWinEventHookHandle(hook, callback));
    }

    private void OnWinEvent(
        nint hook,
        uint eventType,
        nint windowHandle,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTimeMilliseconds)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        WindowTargetSignalKind? kind = MapEvent(eventType, objectId, childId);
        if (kind is null)
        {
            return;
        }

        ulong handle = eventType == NativeWindowInterop.EventSystemForeground
            ? 0
            : ToUnsignedHandle(windowHandle);
        if (handle != 0 &&
            handle != target.WindowHandle &&
            handle != target.RootWindowHandle)
        {
            return;
        }

        signals.Enqueue(new WindowTargetSignal(kind.Value, handle));
    }

    private void RequestReconciliation()
    {
        if (Volatile.Read(ref disposed) == 0)
        {
            signals.Enqueue(new WindowTargetSignal(
                WindowTargetSignalKind.ReconcileRequested,
                target.RootWindowHandle));
        }
    }

    private static WindowTargetSignalKind? MapEvent(
        uint eventType,
        int objectId,
        int childId)
    {
        bool isWindowObject = objectId == NativeWindowInterop.ObjectIdWindow && childId == 0;
        return eventType switch
        {
            NativeWindowInterop.EventSystemForeground =>
                WindowTargetSignalKind.ForegroundChanged,
            NativeWindowInterop.EventSystemMinimizeStart =>
                WindowTargetSignalKind.MinimizeStarted,
            NativeWindowInterop.EventSystemMinimizeEnd =>
                WindowTargetSignalKind.MinimizeEnded,
            NativeWindowInterop.EventObjectDestroy when isWindowObject =>
                WindowTargetSignalKind.Destroyed,
            NativeWindowInterop.EventObjectLocationChange when isWindowObject =>
                WindowTargetSignalKind.LocationChanged,
            NativeWindowInterop.EventObjectCloaked when isWindowObject =>
                WindowTargetSignalKind.Cloaked,
            NativeWindowInterop.EventObjectUncloaked when isWindowObject =>
                WindowTargetSignalKind.Uncloaked,
            _ => null,
        };
    }

    private void DisposeHooks()
    {
        foreach (SafeWinEventHookHandle hook in hooks)
        {
            hook.Dispose();
        }

        hooks.Clear();
    }

    private void RequestHookThreadExit()
    {
        uint threadId = Volatile.Read(ref hookThreadId);
        if (threadId != 0)
        {
            NativeWindowInterop.PostThreadMessage(
                threadId,
                NativeWindowInterop.WindowMessageQuit,
                0,
                nint.Zero);
        }
    }

    private static ulong ToUnsignedHandle(nint handle) => unchecked((ulong)(nuint)handle);
}
