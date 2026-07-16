using System.Windows;
using System.Windows.Interop;
using Tooltail.Application.Windows;
using Tooltail.Desktop.Presentation;
using Tooltail.Platform.Windows.Windowing;

namespace Tooltail.Desktop;

public partial class TetherWindow : Window
{
    private nint windowHandle;

    public TetherWindow(WindowLeaseViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        SourceInitialized += OnSourceInitialized;
    }

    public bool HasNativeHandle => windowHandle != nint.Zero;

    public nint NativeHandle => windowHandle;

    public void Render(WindowBindingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (windowHandle == nint.Zero)
        {
            return;
        }

        WindowTargetSnapshot? target = snapshot.ObservedTarget ?? snapshot.Preview?.Target;
        if (snapshot.Mode is WindowBindingMode.PreviewEligible or WindowBindingMode.Bound &&
            target is not null)
        {
            WindowsOwnedWindowSurfaceController.PlaceNoActivate(windowHandle, target.Bounds);
            return;
        }

        WindowsOwnedWindowSurfaceController.Hide(windowHandle);
    }

    private void OnSourceInitialized(object? sender, EventArgs eventArgs)
    {
        SourceInitialized -= OnSourceInitialized;
        windowHandle = new WindowInteropHelper(this).Handle;
        WindowsOwnedWindowSurfaceController.Configure(
            windowHandle,
            AmbientWindowSurfaceKind.Tether);
        WindowsOwnedWindowSurfaceController.Hide(windowHandle);
    }
}
