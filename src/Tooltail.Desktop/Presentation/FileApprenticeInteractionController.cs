using System.IO;
using Tooltail.Application.FileSkills;

namespace Tooltail.Desktop.Presentation;

public sealed class FileApprenticeInteractionController
{
    private readonly FileApprenticeStartupService startupService;
    private readonly DesktopCompanionSession companionSession;
    private readonly FileApprenticeViewModel viewModel;
    private readonly SafeLabGrantService safeLab;
    private readonly object gate = new();
    private Task? initializationTask;

    public FileApprenticeInteractionController(
        FileApprenticeStartupService startupService,
        DesktopCompanionSession companionSession,
        FileApprenticeViewModel viewModel,
        SafeLabGrantService safeLab)
    {
        ArgumentNullException.ThrowIfNull(startupService);
        ArgumentNullException.ThrowIfNull(companionSession);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(safeLab);
        this.startupService = startupService;
        this.companionSession = companionSession;
        this.viewModel = viewModel;
        this.safeLab = safeLab;
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
            if (result.IsReady)
            {
                Tooltail.Application.Abstractions.LocalFolderGrantStateRecord? activeGrant =
                    result.Workspace!.Grants.FirstOrDefault(grant =>
                        grant.Grant.State == Tooltail.Domain.Permissions.ResourceGrantState.Active);
                if (activeGrant is not null)
                {
                    SafeLabGrantResult restored = safeLab.TryRestore(activeGrant.Grant);
                    if (restored.IsSuccess)
                    {
                        viewModel.ApplyRestoredSafeLab(restored);
                    }
                }
            }
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

    public async Task CreateSafeLabAsync(CancellationToken cancellationToken = default)
    {
        if (!viewModel.CanCreateSafeLab)
        {
            return;
        }

        viewModel.BeginAction("Creating a new Tooltail-owned lab and exact folder grant…");
        try
        {
            SafeLabGrantResult result = await safeLab.CreateAsync(
                companionSession.CompanionId,
                cancellationToken);
            if (result.IsSuccess)
            {
                viewModel.ApplySafeLab(result);
            }
            else
            {
                viewModel.CompleteAction(
                    result.ReasonCode,
                    $"Safe lab stopped without a grant: {result.ReasonCode}.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            viewModel.CompleteAction("safe_lab.cancelled", "Safe lab creation was cancelled.");
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or InvalidOperationException)
        {
            viewModel.CompleteAction(
                "safe_lab.storage_unavailable",
                "Safe lab creation stopped because local storage was unavailable.");
        }
    }
}
