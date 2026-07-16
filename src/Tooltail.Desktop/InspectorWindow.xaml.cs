using System.ComponentModel;
using System.Windows;
using Tooltail.Desktop.Presentation;

namespace Tooltail.Desktop;

public partial class InspectorWindow : Window
{
    private readonly WindowLeaseInteractionController interactions;
    private bool shutdownAllowed;

    public InspectorWindow(
        WindowLeaseViewModel viewModel,
        WindowLeaseInteractionController interactions)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(interactions);
        InitializeComponent();
        DataContext = viewModel;
        this.interactions = interactions;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    public event EventHandler? HomeRequested;

    public event EventHandler? AgentBodyRequested;

    public void PrepareForShutdown() => shutdownAllowed = true;

    private async void OnRefreshTargetsClick(object sender, RoutedEventArgs eventArgs) =>
        await interactions.RefreshTargetsAsync();

    private async void OnAttachSelectedClick(object sender, RoutedEventArgs eventArgs) =>
        await interactions.AttachSelectedAsync();

    private async void OnUnbindClick(object sender, RoutedEventArgs eventArgs) =>
        await interactions.RevokeAsync();

    private async void OnRevokeFolderGrantClick(object sender, RoutedEventArgs eventArgs) =>
        await interactions.RevokeFolderGrantAsync();

    private async void OnReturnHomeClick(object sender, RoutedEventArgs eventArgs) =>
        await interactions.ReturnHomeAsync();

    private void OnPauseClick(object sender, RoutedEventArgs eventArgs) =>
        interactions.RequestPause();

    private void OnCancelClick(object sender, RoutedEventArgs eventArgs) =>
        interactions.RequestCancel();

    private void OnOpenHomeClick(object sender, RoutedEventArgs eventArgs) =>
        HomeRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenAgentBodyClick(object sender, RoutedEventArgs eventArgs) =>
        AgentBodyRequested?.Invoke(this, EventArgs.Empty);

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (!shutdownAllowed)
        {
            eventArgs.Cancel = true;
            Hide();
        }
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        Closing -= OnClosing;
        Closed -= OnClosed;
    }
}
