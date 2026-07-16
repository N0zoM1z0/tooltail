using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Tooltail.Application.Abstractions;
using Tooltail.Application.Windows;
using Tooltail.Desktop.Presentation;
using Tooltail.Platform.Windows.Windowing;

namespace Tooltail.Desktop;

public partial class PetWindow : Window, IDisposable
{
    private const int DragThresholdPixels = 6;

    private readonly WindowBindingService bindingService;
    private readonly IPhysicalPointerSource pointerSource;
    private readonly DesktopCompanionSession companionSession;
    private readonly WindowLeaseViewModel viewModel;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? previewCancellation;
    private HwndSource? source;
    private nint windowHandle;
    private PhysicalScreenPoint homePosition;
    private PhysicalScreenPoint pressPointer;
    private PhysicalScreenPoint pressWindowOrigin;
    private bool pressed;
    private bool dragging;
    private bool resourcesDisposed;

    public PetWindow(
        WindowBindingService bindingService,
        IPhysicalPointerSource pointerSource,
        DesktopCompanionSession companionSession,
        WindowLeaseViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(bindingService);
        ArgumentNullException.ThrowIfNull(pointerSource);
        ArgumentNullException.ThrowIfNull(companionSession);
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        this.bindingService = bindingService;
        this.pointerSource = pointerSource;
        this.companionSession = companionSession;
        this.viewModel = viewModel;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    public event EventHandler? InspectorRequested;

    public event EventHandler? HomeRequested;

    public bool IsDragging => dragging;

    public nint NativeHandle => windowHandle;

    public void Dispose()
    {
        if (resourcesDisposed)
        {
            GC.SuppressFinalize(this);
            return;
        }

        resourcesDisposed = true;
        lifetime.Cancel();
        CancelPreview();
        source?.RemoveHook(WindowMessageHook);
        lifetime.Dispose();
        Closed -= OnClosed;
        GC.SuppressFinalize(this);
    }

    public void ApplyBinding(WindowBindingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (windowHandle == nint.Zero || dragging)
        {
            return;
        }

        if (snapshot.Mode == WindowBindingMode.Bound && snapshot.ObservedTarget is { } target)
        {
            AnchorTo(target.Bounds);
            return;
        }

        if (snapshot.Mode is WindowBindingMode.Home or
            WindowBindingMode.Revoked or
            WindowBindingMode.Expired or
            WindowBindingMode.TargetMinimized)
        {
            WindowsOwnedWindowSurfaceController.MoveNoActivate(windowHandle, homePosition);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs eventArgs)
    {
        SourceInitialized -= OnSourceInitialized;
        windowHandle = new WindowInteropHelper(this).Handle;
        source = HwndSource.FromHwnd(windowHandle);
        source?.AddHook(WindowMessageHook);
        WindowsOwnedWindowSurfaceController.Configure(
            windowHandle,
            AmbientWindowSurfaceKind.Pet);
        PlaceAtHome();
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (windowHandle == nint.Zero ||
            !pointerSource.TryGetCurrentPhysicalPosition(out pressPointer))
        {
            return;
        }

        PhysicalScreenRectangle bounds =
            WindowsOwnedWindowSurfaceController.GetOwnedBounds(windowHandle);
        pressWindowOrigin = new PhysicalScreenPoint(bounds.Left, bounds.Top);
        pressed = true;
        dragging = false;
        CaptureMouse();
        eventArgs.Handled = true;
    }

    private async void OnMouseMove(object sender, MouseEventArgs eventArgs)
    {
        if (!pressed || eventArgs.LeftButton != MouseButtonState.Pressed ||
            !pointerSource.TryGetCurrentPhysicalPosition(out PhysicalScreenPoint pointer))
        {
            return;
        }

        if (!dragging && !ExceedsDragThreshold(pointer))
        {
            return;
        }

        if (!dragging)
        {
            dragging = true;
            WindowBindingActionResult started =
                await bindingService.BeginDragAsync(lifetime.Token);
            if (!started.IsSuccess)
            {
                EndPointerGesture();
                viewModel.ReportAction($"Drag could not start: {started.ReasonCode}.");
                return;
            }
        }

        MoveWithPointer(pointer);
        CancellationToken token = ReplacePreviewCancellation();
        try
        {
            await bindingService.PreviewAtAsync(pointer, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        if (!pressed)
        {
            return;
        }

        bool wasDragging = dragging;
        EndPointerGesture();
        eventArgs.Handled = true;
        if (!wasDragging)
        {
            InspectorRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        CancelPreview();
        if (!pointerSource.TryGetCurrentPhysicalPosition(out PhysicalScreenPoint pointer))
        {
            await bindingService.CancelDragAsync(lifetime.Token);
            return;
        }

        try
        {
            await bindingService.PreviewAtAsync(pointer, lifetime.Token);
            WindowBindingActionResult dropped = await bindingService.DropAsync(
                companionSession.CompanionId,
                lifetime.Token);
            if (!dropped.IsSuccess)
            {
                viewModel.ReportAction($"Drop did not bind: {dropped.ReasonCode}.");
                await bindingService.CancelDragAsync(lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        HomeRequested?.Invoke(this, EventArgs.Empty);
    }

    private nint WindowMessageHook(
        nint window,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        bool hitsVisibleSprite = message != AmbientWindowMessagePolicy.WindowMessageNonClientHitTest ||
            IsVisibleSpritePoint(lParam);
        if (AmbientWindowMessagePolicy.TryHandlePetMessage(
                message,
                hitsVisibleSprite,
                out nint result))
        {
            handled = true;
            return result;
        }

        return nint.Zero;
    }

    private bool IsVisibleSpritePoint(nint packedScreenPoint)
    {
        long packed = packedScreenPoint.ToInt64();
        int screenX = unchecked((short)(packed & 0xFFFF));
        int screenY = unchecked((short)((packed >> 16) & 0xFFFF));
        Point local = PointFromScreen(new Point(screenX, screenY));
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return false;
        }

        double normalizedX = (local.X - (ActualWidth / 2d)) / (ActualWidth * 0.46d);
        double normalizedY = (local.Y - (ActualHeight * 0.43d)) / (ActualHeight * 0.40d);
        bool bodyEllipse = (normalizedX * normalizedX) +
            (normalizedY * normalizedY) <= 1d;
        bool label = local.X >= ActualWidth * 0.08d &&
            local.X <= ActualWidth * 0.92d &&
            local.Y >= ActualHeight * 0.78d &&
            local.Y <= ActualHeight;
        return bodyEllipse || label;
    }

    private bool ExceedsDragThreshold(PhysicalScreenPoint pointer) =>
        Math.Abs(pointer.X - pressPointer.X) >= DragThresholdPixels ||
        Math.Abs(pointer.Y - pressPointer.Y) >= DragThresholdPixels;

    private void MoveWithPointer(PhysicalScreenPoint pointer)
    {
        int offsetX = pressPointer.X - pressWindowOrigin.X;
        int offsetY = pressPointer.Y - pressWindowOrigin.Y;
        WindowsOwnedWindowSurfaceController.MoveNoActivate(
            windowHandle,
            new PhysicalScreenPoint(pointer.X - offsetX, pointer.Y - offsetY));
    }

    private void PlaceAtHome()
    {
        PhysicalScreenPoint reference = pointerSource.TryGetCurrentPhysicalPosition(out var pointer)
            ? pointer
            : new PhysicalScreenPoint(0, 0);
        PhysicalScreenRectangle workArea =
            WindowsOwnedWindowSurfaceController.GetWorkArea(reference);
        PhysicalScreenRectangle petBounds =
            WindowsOwnedWindowSurfaceController.GetOwnedBounds(windowHandle);
        homePosition = new PhysicalScreenPoint(
            workArea.Right - petBounds.Width - 18,
            workArea.Bottom - petBounds.Height - 18);
        WindowsOwnedWindowSurfaceController.MoveNoActivate(windowHandle, homePosition);
    }

    private void AnchorTo(PhysicalScreenRectangle targetBounds)
    {
        PhysicalScreenRectangle petBounds =
            WindowsOwnedWindowSurfaceController.GetOwnedBounds(windowHandle);
        PhysicalScreenPoint targetPoint = new(targetBounds.Right - 1, targetBounds.Top);
        PhysicalScreenRectangle workArea =
            WindowsOwnedWindowSurfaceController.GetWorkArea(targetPoint);
        int left = Math.Clamp(
            targetBounds.Right - petBounds.Width - 12,
            workArea.Left,
            workArea.Right - petBounds.Width);
        int top = Math.Clamp(
            targetBounds.Top + 12,
            workArea.Top,
            workArea.Bottom - petBounds.Height);
        WindowsOwnedWindowSurfaceController.MoveNoActivate(
            windowHandle,
            new PhysicalScreenPoint(left, top));
    }

    private CancellationToken ReplacePreviewCancellation()
    {
        CancelPreview();
        previewCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        return previewCancellation.Token;
    }

    private void CancelPreview()
    {
        previewCancellation?.Cancel();
        previewCancellation?.Dispose();
        previewCancellation = null;
    }

    private void EndPointerGesture()
    {
        pressed = false;
        dragging = false;
        ReleaseMouseCapture();
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        Dispose();
    }
}
