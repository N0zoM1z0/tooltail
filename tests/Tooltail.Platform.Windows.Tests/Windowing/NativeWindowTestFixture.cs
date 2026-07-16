using System.Runtime.InteropServices;

namespace Tooltail.Platform.Windows.Tests.Windowing;

internal sealed partial class NativeWindowTestFixture : IAsyncDisposable
{
    private readonly TaskCompletionSource<nint> ready = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource completed = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private uint threadId;
    private nint windowHandle;
    private int disposed;

    internal NativeWindowTestFixture()
    {
        Title = $"Tooltail synthetic target {Guid.NewGuid():N}";
        thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Tooltail native window test fixture",
        };
        thread.Start();
    }

    internal string Title { get; }

    internal async ValueTask<nint> GetWindowHandleAsync() =>
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));

    internal void MoveWithoutActivation(int left, int top)
    {
        if (!NativeTestInterop.SetWindowPos(
                windowHandle,
                nint.Zero,
                left,
                top,
                0,
                0,
                NativeTestInterop.SetWindowPositionNoSize |
                NativeTestInterop.SetWindowPositionNoZOrder |
                NativeTestInterop.SetWindowPositionNoActivate))
        {
            throw new InvalidOperationException("The synthetic window could not be moved.");
        }
    }

    internal void SetTitle(string title)
    {
        if (!NativeTestInterop.SetWindowText(windowHandle, title))
        {
            throw new InvalidOperationException("The synthetic window title could not be changed.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        uint id = Volatile.Read(ref threadId);
        if (id != 0)
        {
            NativeTestInterop.PostThreadMessage(
                id,
                NativeTestInterop.WindowMessageQuit,
                0,
                nint.Zero);
        }

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private void Run()
    {
        threadId = NativeTestInterop.GetCurrentThreadId();
        try
        {
            windowHandle = NativeTestInterop.CreateWindowEx(
                NativeTestInterop.WindowExtendedStyleNoActivate,
                "STATIC",
                Title,
                NativeTestInterop.WindowStyleOverlappedWindow |
                NativeTestInterop.WindowStyleVisible,
                40,
                40,
                420,
                260,
                nint.Zero,
                nint.Zero,
                nint.Zero,
                nint.Zero);
            if (windowHandle == nint.Zero)
            {
                ready.TrySetException(
                    new InvalidOperationException("The synthetic native window could not be created."));
                return;
            }

            ready.TrySetResult(windowHandle);

            while (true)
            {
                int result = NativeTestInterop.GetMessage(
                    out NativeTestInterop.NativeMessage message,
                    nint.Zero,
                    0,
                    0);
                if (result <= 0)
                {
                    break;
                }

                NativeTestInterop.TranslateMessage(in message);
                NativeTestInterop.DispatchMessage(in message);
            }
        }
        finally
        {
            if (windowHandle != nint.Zero)
            {
                NativeTestInterop.DestroyWindow(windowHandle);
                windowHandle = nint.Zero;
            }

            if (!ready.Task.IsCompleted)
            {
                ready.TrySetException(
                    new InvalidOperationException("The synthetic native window ended before startup."));
            }

            completed.TrySetResult();
        }
    }

    private static partial class NativeTestInterop
    {
        internal const uint WindowExtendedStyleNoActivate = 0x08000000;
        internal const uint WindowStyleOverlappedWindow = 0x00CF0000;
        internal const uint WindowStyleVisible = 0x10000000;
        internal const uint SetWindowPositionNoSize = 0x0001;
        internal const uint SetWindowPositionNoZOrder = 0x0004;
        internal const uint SetWindowPositionNoActivate = 0x0010;
        internal const uint WindowMessageQuit = 0x0012;

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativePoint
        {
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeMessage
        {
            internal nint WindowHandle;
            internal uint Message;
            internal nuint WParam;
            internal nint LParam;
            internal uint Time;
            internal NativePoint Point;
            internal uint Private;
        }

        [LibraryImport(
            "user32.dll",
            EntryPoint = "CreateWindowExW",
            SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        internal static partial nint CreateWindowEx(
            uint extendedStyle,
            string className,
            string windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            nint parent,
            nint menu,
            nint instance,
            nint parameter);

        [LibraryImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DestroyWindow(nint windowHandle);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetWindowPos(
            nint windowHandle,
            nint insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [LibraryImport(
            "user32.dll",
            EntryPoint = "SetWindowTextW",
            SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetWindowText(nint windowHandle, string text);

        [LibraryImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
        internal static partial int GetMessage(
            out NativeMessage message,
            nint windowHandle,
            uint messageFilterMinimum,
            uint messageFilterMaximum);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool TranslateMessage(in NativeMessage message);

        [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
        internal static partial nint DispatchMessage(in NativeMessage message);

        [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool PostThreadMessage(
            uint threadId,
            uint message,
            nuint wParam,
            nint lParam);

        [LibraryImport("kernel32.dll")]
        internal static partial uint GetCurrentThreadId();
    }
}
