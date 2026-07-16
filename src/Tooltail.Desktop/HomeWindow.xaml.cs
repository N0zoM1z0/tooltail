using System.Windows;
using Tooltail.Desktop.Presentation;

namespace Tooltail.Desktop;

public partial class HomeWindow : Window
{
    private readonly WindowLeaseInteractionController interactions;
    private bool loaded;

    public HomeWindow(
        WindowLeaseViewModel viewModel,
        WindowLeaseInteractionController interactions)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(interactions);
        InitializeComponent();
        DataContext = viewModel;
        this.interactions = interactions;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public event EventHandler? InspectorRequested;

    public event EventHandler? AgentBodyRequested;

    private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (loaded)
        {
            return;
        }

        loaded = true;
        await interactions.RefreshTargetsAsync();
    }

    private async void OnRefreshTargetsClick(object sender, RoutedEventArgs eventArgs) =>
        await interactions.RefreshTargetsAsync();

    private async void OnAttachSelectedClick(object sender, RoutedEventArgs eventArgs) =>
        await interactions.AttachSelectedAsync();

    private void OnOpenInspectorClick(object sender, RoutedEventArgs eventArgs) =>
        InspectorRequested?.Invoke(this, EventArgs.Empty);

    private async void OnUnbindClick(object sender, RoutedEventArgs eventArgs) =>
        await interactions.RevokeAsync();

    private async void OnReturnHomeClick(object sender, RoutedEventArgs eventArgs) =>
        await interactions.ReturnHomeAsync();

    private void OnPauseClick(object sender, RoutedEventArgs eventArgs) =>
        interactions.RequestPause();

    private void OnCancelClick(object sender, RoutedEventArgs eventArgs) =>
        interactions.RequestCancel();

    private void OnOpenAgentBodyClick(object sender, RoutedEventArgs eventArgs) =>
        AgentBodyRequested?.Invoke(this, EventArgs.Empty);

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        Loaded -= OnLoaded;
        Closed -= OnClosed;
    }
}
