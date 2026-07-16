using Tooltail.Application.Windows;

namespace Tooltail.Platform.Windows.Windowing;

public enum AmbientWindowSurfaceKind
{
    Pet,
    Tether,
}

/// <summary>
/// Applies no-activate styles and physical placement only to HWNDs owned by this Tooltail
/// process. Target HWNDs are never accepted by a mutating method.
/// </summary>
public static class WindowsOwnedWindowSurfaceController
{
    private const uint SetWindowPositionNoSize = 0x0001;
    private const uint SetWindowPositionNoMove = 0x0002;
    private const uint SetWindowPositionNoActivate = 0x0010;
    private const uint SetWindowPositionShowWindow = 0x0040;
    private const uint SetWindowPositionNoOwnerZOrder = 0x0200;
    private const int ShowWindowHide = 0;
    private const int ShowWindowWithoutActivation = 4;
    private static readonly nint WindowTopmost = new(-1);

    public static void Configure(nint ownedWindow, AmbientWindowSurfaceKind kind)
    {
        EnsureWindows();
        EnsureOwnedWindow(ownedWindow);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        long extendedStyle = NativeWindowInterop.GetWindowStyle(
            ownedWindow,
            NativeWindowInterop.WindowLongExtendedStyle);
        extendedStyle |= NativeWindowInterop.WindowExtendedStyleToolWindow |
            NativeWindowInterop.WindowExtendedStyleNoActivate;
        extendedStyle &= ~NativeWindowInterop.WindowExtendedStyleAppWindow;
        if (kind == AmbientWindowSurfaceKind.Tether)
        {
            extendedStyle |= NativeWindowInterop.WindowExtendedStyleTransparent;
        }
        else
        {
            extendedStyle &= ~NativeWindowInterop.WindowExtendedStyleTransparent;
        }

        NativeWindowInterop.SetWindowStyle(
            ownedWindow,
            NativeWindowInterop.WindowLongExtendedStyle,
            extendedStyle);
    }

    public static void MoveNoActivate(nint ownedWindow, PhysicalScreenPoint topLeft)
    {
        EnsureWindows();
        EnsureOwnedWindow(ownedWindow);
        if (!NativeWindowInterop.SetWindowPos(
                ownedWindow,
                WindowTopmost,
                topLeft.X,
                topLeft.Y,
                0,
                0,
                SetWindowPositionNoSize |
                SetWindowPositionNoActivate |
                SetWindowPositionNoOwnerZOrder |
                SetWindowPositionShowWindow))
        {
            throw new InvalidOperationException("The Tooltail-owned ambient window could not be moved.");
        }
    }

    public static void PlaceNoActivate(nint ownedWindow, PhysicalScreenRectangle bounds)
    {
        EnsureWindows();
        EnsureOwnedWindow(ownedWindow);
        if (!NativeWindowInterop.SetWindowPos(
                ownedWindow,
                WindowTopmost,
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height,
                SetWindowPositionNoActivate |
                SetWindowPositionNoOwnerZOrder |
                SetWindowPositionShowWindow))
        {
            throw new InvalidOperationException("The Tooltail-owned ambient window could not be placed.");
        }
    }

    public static void ShowNoActivate(nint ownedWindow)
    {
        EnsureWindows();
        EnsureOwnedWindow(ownedWindow);
        NativeWindowInterop.ShowWindow(ownedWindow, ShowWindowWithoutActivation);
        if (!NativeWindowInterop.SetWindowPos(
                ownedWindow,
                WindowTopmost,
                0,
                0,
                0,
                0,
                SetWindowPositionNoMove |
                SetWindowPositionNoSize |
                SetWindowPositionNoActivate |
                SetWindowPositionNoOwnerZOrder |
                SetWindowPositionShowWindow))
        {
            throw new InvalidOperationException("The Tooltail-owned ambient window could not be shown.");
        }
    }

    public static void Hide(nint ownedWindow)
    {
        EnsureWindows();
        EnsureOwnedWindow(ownedWindow);
        NativeWindowInterop.ShowWindow(ownedWindow, ShowWindowHide);
    }

    public static PhysicalScreenRectangle GetOwnedBounds(nint ownedWindow)
    {
        EnsureWindows();
        EnsureOwnedWindow(ownedWindow);
        if (!NativeWindowInterop.GetWindowRect(
                ownedWindow,
                out NativeWindowInterop.NativeRectangle rectangle) ||
            rectangle.Right <= rectangle.Left ||
            rectangle.Bottom <= rectangle.Top)
        {
            throw new InvalidOperationException("The Tooltail-owned ambient window has no usable bounds.");
        }

        return new PhysicalScreenRectangle(
            rectangle.Left,
            rectangle.Top,
            rectangle.Right,
            rectangle.Bottom);
    }

    public static PhysicalScreenRectangle GetWorkArea(PhysicalScreenPoint physicalPoint)
    {
        EnsureWindows();
        NativeWindowInterop.NativePoint point = new()
        {
            X = physicalPoint.X,
            Y = physicalPoint.Y,
        };
        nint monitor = NativeWindowInterop.MonitorFromPoint(
            point,
            NativeWindowInterop.MonitorDefaultToNearest);
        NativeWindowInterop.NativeMonitorInfo information = new()
        {
            Size = checked((uint)System.Runtime.InteropServices.Marshal.SizeOf<
                NativeWindowInterop.NativeMonitorInfo>()),
        };
        if (monitor == nint.Zero ||
            !NativeWindowInterop.GetMonitorInfo(monitor, ref information) ||
            information.WorkArea.Right <= information.WorkArea.Left ||
            information.WorkArea.Bottom <= information.WorkArea.Top)
        {
            throw new InvalidOperationException("The physical monitor work area is unavailable.");
        }

        return new PhysicalScreenRectangle(
            information.WorkArea.Left,
            information.WorkArea.Top,
            information.WorkArea.Right,
            information.WorkArea.Bottom);
    }

    public static long ReadExtendedStyle(nint ownedWindow)
    {
        EnsureWindows();
        EnsureOwnedWindow(ownedWindow);
        return NativeWindowInterop.GetWindowStyle(
            ownedWindow,
            NativeWindowInterop.WindowLongExtendedStyle);
    }

    public static bool IsForeground(nint ownedWindow)
    {
        EnsureWindows();
        EnsureOwnedWindow(ownedWindow);
        return NativeWindowInterop.GetForegroundWindow() == ownedWindow;
    }

    public static bool HasRequiredStyle(long extendedStyle, AmbientWindowSurfaceKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        bool common =
            (extendedStyle & NativeWindowInterop.WindowExtendedStyleToolWindow) != 0 &&
            (extendedStyle & NativeWindowInterop.WindowExtendedStyleNoActivate) != 0 &&
            (extendedStyle & NativeWindowInterop.WindowExtendedStyleAppWindow) == 0;
        bool isTransparent =
            (extendedStyle & NativeWindowInterop.WindowExtendedStyleTransparent) != 0;
        return common && isTransparent == (kind == AmbientWindowSurfaceKind.Tether);
    }

    private static void EnsureOwnedWindow(nint windowHandle)
    {
        if (windowHandle == nint.Zero || !NativeWindowInterop.IsWindow(windowHandle))
        {
            throw new ArgumentException("A live Tooltail-owned HWND is required.", nameof(windowHandle));
        }

        NativeWindowInterop.GetWindowThreadProcessId(windowHandle, out uint processId);
        if (processId != Environment.ProcessId)
        {
            throw new InvalidOperationException(
                "Ambient window mutation is confined to HWNDs owned by this Tooltail process.");
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Ambient HWND surfaces require Windows.");
        }
    }
}
