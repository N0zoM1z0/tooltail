using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tooltail.Application;
using Tooltail.Application.Abstractions;
using Tooltail.Application.Windows;
using Tooltail.Desktop.Controls;
using Tooltail.Desktop.Presentation;
using Tooltail.Infrastructure.Sqlite;
using Tooltail.Platform.Windows.Windowing;

namespace Tooltail.Desktop;

public partial class App : System.Windows.Application
{
    private IHost? host;
    private HomeWindow? homeWindow;
    private InspectorWindow? inspectorWindow;
    private MainWindow? agentBodyWindow;
    private WindowSurfaceCoordinator? surfaceCoordinator;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                ApplicationName = typeof(App).Assembly.GetName().Name,
                Args = [],
            });

        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Services.AddTooltailApplication();
        builder.Services.AddSingleton(
            new SqliteDatabaseOptions(
                ResolveDatabasePath(e.Args),
                typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0"));
        builder.Services.AddSingleton<TooltailSqliteDatabase>();
        builder.Services.AddSingleton<SqliteFileSkillStateStore>();
        builder.Services.AddSingleton<IFileSkillStateStore>(static services =>
            services.GetRequiredService<SqliteFileSkillStateStore>());
        builder.Services.AddSingleton<SqliteExecutionJournalStore>();
        builder.Services.AddSingleton<IExecutionJournalStore>(static services =>
            services.GetRequiredService<SqliteExecutionJournalStore>());
        builder.Services.AddSingleton<IExecutionJournalReader>(static services =>
            services.GetRequiredService<SqliteExecutionJournalStore>());
        builder.Services.AddSingleton<WindowsWindowSystem>();
        builder.Services.AddSingleton<IWindowSystem>(static services =>
            services.GetRequiredService<WindowsWindowSystem>());
        builder.Services.AddSingleton<IPhysicalPointerSource>(static services =>
            services.GetRequiredService<WindowsWindowSystem>());
        builder.Services.AddSingleton<ICoordinateSpace, WindowsCoordinateSpace>();
        builder.Services.AddSingleton(WindowBindingPolicy.Default);
        builder.Services.AddSingleton<WindowBindingService>();
        builder.Services.AddSingleton<DesktopCompanionSession>();
        builder.Services.AddSingleton<WindowLeaseViewModel>();
        builder.Services.AddSingleton<WindowLeaseInteractionController>();
        builder.Services.AddSingleton<FileApprenticeViewModel>();
        builder.Services.AddSingleton<FileApprenticeInteractionController>();
        builder.Services.AddSingleton<WindowSurfaceCoordinator>();
        builder.Services.AddSingleton<HomeWindow>();
        builder.Services.AddSingleton<InspectorWindow>();
        builder.Services.AddSingleton<PetWindow>();
        builder.Services.AddSingleton<TetherWindow>();
        builder.Services.AddTransient<MainWindow>();

        host = builder.Build();

        bool legacySmoke = e.Args.Contains("--smoke-test", StringComparer.Ordinal) ||
            e.Args.Contains("--skill-card-smoke-test", StringComparer.Ordinal) ||
            e.Args.Contains("--agent-body-smoke-test", StringComparer.Ordinal);
        if (!legacySmoke)
        {
            _ = host.Services.GetRequiredService<TooltailSqliteDatabase>()
                .InitializeAsync()
                .GetAwaiter()
                .GetResult();
        }

        host.Start();
        if (!legacySmoke)
        {
            StartWindowShell(e.Args);
            return;
        }

        MainWindow mainWindow = host.Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        if (e.Args.Contains("--skill-card-smoke-test", StringComparer.Ordinal))
        {
            mainWindow.Content = new SkillCardControl();
        }

        if (e.Args.Contains("--agent-body-smoke-test", StringComparer.Ordinal))
        {
            mainWindow.ConfigureAgentBodySmokeTest();
        }

        mainWindow.ContentRendered += CloseAfterSmokeRender;

        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            host?.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        finally
        {
            host?.Dispose();
            base.OnExit(e);
        }
    }

    private void CloseAfterSmokeRender(object? sender, EventArgs e)
    {
        if (MainWindow is null)
        {
            return;
        }

        MainWindow.ContentRendered -= CloseAfterSmokeRender;
        MainWindow.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            () => MainWindow.Close());
    }

    private void StartWindowShell(IReadOnlyCollection<string> args)
    {
        homeWindow = host!.Services.GetRequiredService<HomeWindow>();
        inspectorWindow = host.Services.GetRequiredService<InspectorWindow>();
        PetWindow petWindow = host.Services.GetRequiredService<PetWindow>();
        TetherWindow tetherWindow = host.Services.GetRequiredService<TetherWindow>();
        surfaceCoordinator = host.Services.GetRequiredService<WindowSurfaceCoordinator>();

        MainWindow = homeWindow;
        homeWindow.InspectorRequested += OnInspectorRequested;
        homeWindow.AgentBodyRequested += OnAgentBodyRequested;
        homeWindow.Closing += OnHomeClosing;
        inspectorWindow.HomeRequested += OnHomeRequested;
        inspectorWindow.AgentBodyRequested += OnAgentBodyRequested;
        petWindow.InspectorRequested += OnInspectorRequested;
        petWindow.HomeRequested += OnHomeRequested;

        homeWindow.Show();
        inspectorWindow.Owner = homeWindow;
        surfaceCoordinator.Start(petWindow, tetherWindow);
        if (args.Contains("--window-shell-smoke-test", StringComparer.Ordinal))
        {
            homeWindow.ContentRendered += CloseAfterWindowShellSmoke;
        }
    }

    private void OnInspectorRequested(object? sender, EventArgs eventArgs)
    {
        if (inspectorWindow is null)
        {
            return;
        }

        if (!inspectorWindow.IsVisible)
        {
            inspectorWindow.Show();
        }

        inspectorWindow.WindowState = WindowState.Normal;
        inspectorWindow.Activate();
        inspectorWindow.Focus();
    }

    private void OnHomeRequested(object? sender, EventArgs eventArgs)
    {
        if (homeWindow is null)
        {
            return;
        }

        if (!homeWindow.IsVisible)
        {
            homeWindow.Show();
        }

        homeWindow.WindowState = WindowState.Normal;
        homeWindow.Activate();
        homeWindow.Focus();
    }

    private void OnAgentBodyRequested(object? sender, EventArgs eventArgs)
    {
        if (agentBodyWindow is null || !agentBodyWindow.IsLoaded)
        {
            agentBodyWindow = host!.Services.GetRequiredService<MainWindow>();
            agentBodyWindow.Closed += OnAgentBodyClosed;
            agentBodyWindow.Show();
        }

        agentBodyWindow.WindowState = WindowState.Normal;
        agentBodyWindow.Activate();
        agentBodyWindow.Focus();
    }

    private void OnAgentBodyClosed(object? sender, EventArgs eventArgs)
    {
        if (agentBodyWindow is not null)
        {
            agentBodyWindow.Closed -= OnAgentBodyClosed;
            agentBodyWindow = null;
        }
    }

    private void OnHomeClosing(object? sender, System.ComponentModel.CancelEventArgs eventArgs)
    {
        inspectorWindow?.PrepareForShutdown();
        surfaceCoordinator?.Dispose();
    }

    private void CloseAfterWindowShellSmoke(object? sender, EventArgs eventArgs)
    {
        if (homeWindow is null)
        {
            return;
        }

        homeWindow.ContentRendered -= CloseAfterWindowShellSmoke;
        homeWindow.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            () =>
            {
                _ = VerifyAndCloseWindowShellSmokeAsync();
            });
    }

    private async Task VerifyAndCloseWindowShellSmokeAsync()
    {
        try
        {
            await host!.Services
                .GetRequiredService<FileApprenticeInteractionController>()
                .InitializeAsync();
            if (!host.Services.GetRequiredService<FileApprenticeViewModel>().IsReady)
            {
                throw new InvalidOperationException(
                    "The persisted File Apprentice startup state was not ready.");
            }

            surfaceCoordinator!.VerifyAmbientStyles();
            OnInspectorRequested(this, EventArgs.Empty);
            inspectorWindow!.UpdateLayout();
        }
        catch (InvalidOperationException)
        {
            Environment.ExitCode = 1;
        }
        finally
        {
            inspectorWindow?.PrepareForShutdown();
            homeWindow?.Close();
        }
    }

    private static string ResolveDatabasePath(IReadOnlyCollection<string> args)
    {
        bool smoke = args.Any(static argument =>
            argument.EndsWith("-smoke-test", StringComparison.Ordinal));
        string root = smoke
            ? Path.Combine(
                Path.GetTempPath(),
                "Tooltail",
                "Smoke",
                Environment.ProcessId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture))
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Tooltail");
        return Path.Combine(root, "state", "tooltail.db");
    }
}
