using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tooltail.Platform.Windows.Windowing;

internal static partial class NativeWindowInterop
{
    internal const int AncestorRoot = 2;
    internal const int AncestorRootOwner = 3;
    internal const int WindowLongStyle = -16;
    internal const int WindowLongExtendedStyle = -20;
    internal const long WindowStyleChild = 0x40000000L;
    internal const long WindowExtendedStyleTransparent = 0x00000020L;
    internal const long WindowExtendedStyleToolWindow = 0x00000080L;
    internal const long WindowExtendedStyleAppWindow = 0x00040000L;
    internal const long WindowExtendedStyleNoActivate = 0x08000000L;
    internal const uint ProcessQueryLimitedInformation = 0x00001000;
    internal const uint TokenQuery = 0x0008;
    internal const int TokenIntegrityLevel = 25;
    internal const uint DwmWindowAttributeExtendedFrameBounds = 9;
    internal const uint DwmWindowAttributeCloaked = 14;
    internal const uint EventSystemForeground = 0x0003;
    internal const uint EventSystemMinimizeStart = 0x0016;
    internal const uint EventSystemMinimizeEnd = 0x0017;
    internal const uint EventObjectDestroy = 0x8001;
    internal const uint EventObjectLocationChange = 0x800B;
    internal const uint EventObjectCloaked = 0x8017;
    internal const uint EventObjectUncloaked = 0x8018;
    internal const int ObjectIdWindow = 0;
    internal const uint WinEventOutOfContext = 0x0000;
    internal const uint WinEventSkipOwnProcess = 0x0002;
    internal const uint SecurityMandatoryMediumRid = 0x00002000;
    internal const uint PeekMessageNoRemove = 0x0000;
    internal const uint WindowMessageQuit = 0x0012;
    internal const uint MonitorDefaultToNearest = 0x00000002;
    internal static readonly nint DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate bool EnumWindowsCallback(nint windowHandle, nint parameter);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void WinEventCallback(
        nint hook,
        uint eventType,
        nint windowHandle,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTimeMilliseconds);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRectangle
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeFileTime
    {
        internal uint LowDateTime;
        internal uint HighDateTime;

        internal readonly long ToInt64() =>
            unchecked((long)(((ulong)HighDateTime << 32) | LowDateTime));
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

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeMonitorInfo
    {
        internal uint Size;
        internal NativeRectangle Monitor;
        internal NativeRectangle WorkArea;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SidAndAttributes
    {
        internal nint Sid;
        internal uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenMandatoryLabel
    {
        internal SidAndAttributes Label;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(nint callback, nint parameter);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint windowHandle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint windowHandle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint windowHandle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsHungAppWindow(nint windowHandle);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(
        nint windowHandle,
        out NativeRectangle rectangle);

    [LibraryImport("user32.dll")]
    internal static partial nint GetAncestor(nint windowHandle, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetWindowThreadProcessId(
        nint windowHandle,
        out uint processId);

    [LibraryImport("user32.dll")]
    internal static partial nint GetShellWindow();

    [LibraryImport("user32.dll")]
    internal static partial nint GetDesktopWindow();

    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static partial nint GetWindowLongPointer(nint windowHandle, int index);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    internal static partial int GetWindowLong32(nint windowHandle, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static partial nint SetWindowLongPointer(
        nint windowHandle,
        int index,
        nint newValue);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    internal static partial int SetWindowLong32(
        nint windowHandle,
        int index,
        int newValue);

    [LibraryImport(
        "user32.dll",
        EntryPoint = "GetClassNameW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetClassName(
        nint windowHandle,
        [Out] char[] className,
        int maximumCount);

    [LibraryImport(
        "user32.dll",
        EntryPoint = "GetWindowTextW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetWindowText(
        nint windowHandle,
        [Out] char[] text,
        int maximumCount);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint windowHandle);

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

    [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint windowHandle, int command);

    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromPoint(
        NativePoint point,
        uint flags);

    [LibraryImport("user32.dll")]
    internal static partial nint GetThreadDpiAwarenessContext();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AreDpiAwarenessContextsEqual(
        nint first,
        nint second);

    [LibraryImport(
        "user32.dll",
        EntryPoint = "GetMonitorInfoW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfo(
        nint monitor,
        ref NativeMonitorInfo monitorInfo);

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

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PeekMessage(
        out NativeMessage message,
        nint windowHandle,
        uint messageFilterMinimum,
        uint messageFilterMaximum,
        uint removeMessage);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostThreadMessage(
        uint threadId,
        uint message,
        nuint wParam,
        nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint SetWinEventHook(
        uint eventMinimum,
        uint eventMaximum,
        nint eventHookModule,
        nint callback,
        uint processId,
        uint threadId,
        uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWinEvent(nint hook);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(
        nint windowHandle,
        uint attribute,
        out int value,
        uint valueSize);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    internal static partial int DwmGetWindowRectangleAttribute(
        nint windowHandle,
        uint attribute,
        out NativeRectangle value,
        uint valueSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessTimes(
        SafeProcessHandle process,
        out NativeFileTime creationTime,
        out NativeFileTime exitTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "QueryFullProcessImageNameW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        [Out] char[] executableName,
        ref uint size);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(
        SafeProcessHandle process,
        uint desiredAccess,
        out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTokenInformation(
        SafeAccessTokenHandle token,
        int tokenInformationClass,
        nint tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [LibraryImport("advapi32.dll")]
    internal static partial nint GetSidSubAuthorityCount(nint sid);

    [LibraryImport("advapi32.dll")]
    internal static partial nint GetSidSubAuthority(nint sid, uint subAuthority);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();

    internal static long GetWindowStyle(nint windowHandle, int index) =>
        nint.Size == 8
            ? GetWindowLongPointer(windowHandle, index).ToInt64()
            : GetWindowLong32(windowHandle, index);

    internal static void SetWindowStyle(nint windowHandle, int index, long value)
    {
        if (nint.Size == 8)
        {
            SetWindowLongPointer(windowHandle, index, unchecked((nint)value));
        }
        else
        {
            SetWindowLong32(windowHandle, index, checked((int)value));
        }
    }
}

internal sealed class SafeWinEventHookHandle : SafeHandle
{
    private readonly NativeWindowInterop.WinEventCallback callbackRoot;

    internal SafeWinEventHookHandle(
        nint handle,
        NativeWindowInterop.WinEventCallback callbackRoot)
        : base(nint.Zero, ownsHandle: true)
    {
        this.callbackRoot = callbackRoot;
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == nint.Zero || handle == new nint(-1);

    protected override bool ReleaseHandle()
    {
        bool released = NativeWindowInterop.UnhookWinEvent(handle);
        GC.KeepAlive(callbackRoot);
        return released;
    }
}
