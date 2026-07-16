namespace Tooltail.Platform.Windows.Windowing;

public static class WindowsDpiAwareness
{
    public static bool IsCurrentThreadPerMonitorV2()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        nint current = NativeWindowInterop.GetThreadDpiAwarenessContext();
        return current != nint.Zero &&
            NativeWindowInterop.AreDpiAwarenessContextsEqual(
                current,
                NativeWindowInterop.DpiAwarenessContextPerMonitorAwareV2);
    }
}
