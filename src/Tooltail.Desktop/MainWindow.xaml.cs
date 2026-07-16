using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Tooltail.Application.Abstractions;
using Tooltail.Desktop.Presentation;

namespace Tooltail.Desktop;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer playbackTimer;
    private bool initialized;
    private bool useAgentBodySmokeFrame;

    public MainWindow(IClock clock)
    {
        InitializeComponent();
        ViewModel = new AgentBodyWorkbenchViewModel(
            clock,
            reducedMotion: !SystemParameters.ClientAreaAnimation);
        DataContext = ViewModel;
        playbackTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = ViewModel.PlaybackInterval,
        };
        playbackTimer.Tick += OnPlaybackTick;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public AgentBodyWorkbenchViewModel ViewModel { get; }

    public void ConfigureAgentBodySmokeTest() => useAgentBodySmokeFrame = true;

    private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        Loaded -= OnLoaded;
        await ViewModel.InitializeAsync();
        if (useAgentBodySmokeFrame)
        {
            SimulatorTraceChoice parallel = ViewModel.TraceChoices.Single(
                static choice => choice.Name == "parallel-two-units");
            await ViewModel.SelectTraceAsync(parallel);
            ViewModel.StepForward();
            ViewModel.StepForward();
            ViewModel.StepForward();
        }

        initialized = true;
    }

    private async void OnTraceSelectionChanged(
        object sender,
        SelectionChangedEventArgs eventArgs)
    {
        if (!initialized || sender is not ComboBox
            {
                SelectedItem: SimulatorTraceChoice choice,
            })
        {
            return;
        }

        StopPlayback();
        await ViewModel.SelectTraceAsync(choice);
    }

    private void OnSpeedSelectionChanged(
        object sender,
        SelectionChangedEventArgs eventArgs)
    {
        playbackTimer.Interval = ViewModel.PlaybackInterval;
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel.IsPlaying)
        {
            StopPlayback();
            return;
        }

        ViewModel.SetPlaybackActive(active: true);
        if (ViewModel.IsPlaying)
        {
            playbackTimer.Interval = ViewModel.PlaybackInterval;
            playbackTimer.Start();
        }
    }

    private void OnNextStepClick(object sender, RoutedEventArgs eventArgs)
    {
        StopPlayback();
        ViewModel.StepForward();
    }

    private void OnResetClick(object sender, RoutedEventArgs eventArgs)
    {
        StopPlayback();
        ViewModel.Reset();
    }

    private void OnPlaybackTick(object? sender, EventArgs eventArgs)
    {
        if (!ViewModel.StepForward() || !ViewModel.IsPlaying)
        {
            StopPlayback();
        }
    }

    private void StopPlayback()
    {
        playbackTimer.Stop();
        ViewModel.SetPlaybackActive(active: false);
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        StopPlayback();
        playbackTimer.Tick -= OnPlaybackTick;
    }
}
