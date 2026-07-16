using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tooltail.Application;
using Tooltail.Desktop.Controls;

namespace Tooltail.Desktop;

public partial class App : System.Windows.Application
{
    private IHost? host;

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
        builder.Services.AddSingleton<MainWindow>();

        host = builder.Build();
        host.Start();

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

        if (e.Args.Contains("--smoke-test", StringComparer.Ordinal) ||
            e.Args.Contains("--skill-card-smoke-test", StringComparer.Ordinal) ||
            e.Args.Contains("--agent-body-smoke-test", StringComparer.Ordinal))
        {
            mainWindow.ContentRendered += CloseAfterSmokeRender;
        }

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
}
