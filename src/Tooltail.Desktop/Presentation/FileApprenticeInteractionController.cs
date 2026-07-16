using System.IO;
using Tooltail.Application.FileSkills;

namespace Tooltail.Desktop.Presentation;

public sealed class FileApprenticeInteractionController
{
    private readonly FileApprenticeStartupService startupService;
    private readonly DesktopCompanionSession companionSession;
    private readonly FileApprenticeViewModel viewModel;
    private readonly object gate = new();
    private Task? initializationTask;

    public FileApprenticeInteractionController(
        FileApprenticeStartupService startupService,
        DesktopCompanionSession companionSession,
        FileApprenticeViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(startupService);
        ArgumentNullException.ThrowIfNull(companionSession);
        ArgumentNullException.ThrowIfNull(viewModel);
        this.startupService = startupService;
        this.companionSession = companionSession;
        this.viewModel = viewModel;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            initializationTask ??= InitializeCoreAsync(cancellationToken);
            return initializationTask;
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            FileApprenticeStartupResult result = await startupService.InitializeAsync(
                cancellationToken);
            if (result.IsReady)
            {
                companionSession.Restore(result.Workspace!.Companion.Id);
            }

            viewModel.Apply(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            viewModel.Apply(
                FileApprenticeStartupResult.Failure("startup.cancelled"));
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or InvalidOperationException)
        {
            viewModel.Apply(
                FileApprenticeStartupResult.Failure("startup.local_state_unavailable"));
        }
    }
}
