using System.Windows.Threading;
using Tooltail.Application.Windows;
using Tooltail.Platform.Windows.Windowing;

namespace Tooltail.Desktop.Presentation;

public sealed class WindowSurfaceCoordinator(
    WindowBindingService bindingService,
    WindowLeaseViewModel viewModel) : IDisposable
{
    private readonly Dispatcher dispatcher = System.Windows.Application.Current.Dispatcher;
    private PetWindow? petWindow;
    private TetherWindow? tetherWindow;
    private long appliedRevision = -1;
    private bool disposed;

    public void Start(PetWindow pet, TetherWindow tether)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(pet);
        ArgumentNullException.ThrowIfNull(tether);
        if (petWindow is not null || tetherWindow is not null)
        {
            throw new InvalidOperationException("The ambient window surfaces are already attached.");
        }

        petWindow = pet;
        tetherWindow = tether;
        bindingService.Changed += OnBindingChanged;
        tether.Show();
        pet.Show();
        Apply(bindingService.Current);
    }

    public void VerifyAmbientStyles()
    {
        if (petWindow?.NativeHandle is not { } petHandle || petHandle == nint.Zero ||
            tetherWindow?.NativeHandle is not { } tetherHandle || tetherHandle == nint.Zero)
        {
            throw new InvalidOperationException("The ambient HWNDs have not been initialized.");
        }

        long petStyle = WindowsOwnedWindowSurfaceController.ReadExtendedStyle(petHandle);
        long tetherStyle = WindowsOwnedWindowSurfaceController.ReadExtendedStyle(tetherHandle);
        if (!WindowsDpiAwareness.IsCurrentThreadPerMonitorV2() ||
            !WindowsOwnedWindowSurfaceController.HasRequiredStyle(
                petStyle,
                AmbientWindowSurfaceKind.Pet) ||
            !WindowsOwnedWindowSurfaceController.HasRequiredStyle(
                tetherStyle,
                AmbientWindowSurfaceKind.Tether) ||
            WindowsOwnedWindowSurfaceController.IsForeground(petHandle) ||
            WindowsOwnedWindowSurfaceController.IsForeground(tetherHandle))
        {
            throw new InvalidOperationException("The ambient HWND styles are not fail-safe.");
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        bindingService.Changed -= OnBindingChanged;
    }

    private void OnBindingChanged(object? sender, WindowBindingChangedEventArgs eventArgs)
    {
        if (disposed)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            Apply(eventArgs.Snapshot);
            return;
        }

        _ = dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            () => Apply(eventArgs.Snapshot));
    }

    private void Apply(WindowBindingSnapshot snapshot)
    {
        if (disposed || snapshot.Revision < appliedRevision)
        {
            return;
        }

        appliedRevision = snapshot.Revision;
        viewModel.Apply(snapshot);
        tetherWindow?.Render(snapshot);
        petWindow?.ApplyBinding(snapshot);
    }
}
