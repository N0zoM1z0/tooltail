using Tooltail.Application.Windows;

namespace Tooltail.Desktop.Presentation;

public sealed class WindowLeaseInteractionController(
    WindowBindingService bindingService,
    DesktopCompanionSession companionSession,
    WindowLeaseViewModel viewModel,
    FileApprenticeInteractionController fileApprenticeInteractions)
{
    private int refreshActive;

    public async Task RefreshTargetsAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref refreshActive, 1) != 0)
        {
            return;
        }

        viewModel.BeginTargetRefresh();
        try
        {
            IReadOnlyList<WindowTargetSnapshot> targets =
                await bindingService.EnumerateKeyboardTargetsAsync(cancellationToken);
            viewModel.CompleteTargetRefresh(targets);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            viewModel.FailTargetRefresh("window_target.refresh_cancelled");
        }
        catch (Exception exception) when (exception is InvalidOperationException or
            NotSupportedException or UnauthorizedAccessException)
        {
            viewModel.FailTargetRefresh("window_target.refresh_failed");
        }
        finally
        {
            Interlocked.Exchange(ref refreshActive, 0);
        }
    }

    public async Task AttachSelectedAsync(CancellationToken cancellationToken = default)
    {
        if (viewModel.SelectedTarget is not { } choice)
        {
            viewModel.ReportAction("Select an eligible target before attaching.");
            return;
        }

        WindowBindingActionResult result = await bindingService.AttachFromKeyboardAsync(
            companionSession.CompanionId,
            choice.Target,
            cancellationToken);
        if (!result.IsSuccess)
        {
            viewModel.ReportAction($"Attach failed: {result.ReasonCode}.");
        }
    }

    public async Task RevokeAsync(CancellationToken cancellationToken = default)
    {
        WindowBindingActionResult result =
            await bindingService.RevokeByUserAsync(cancellationToken);
        if (!result.IsSuccess)
        {
            viewModel.ReportAction("No active window context needed revocation.");
        }
    }

    public async Task ReturnHomeAsync(CancellationToken cancellationToken = default)
    {
        WindowBindingActionResult result = await bindingService.ReturnHomeAsync(cancellationToken);
        if (!result.IsSuccess)
        {
            viewModel.ReportAction($"Return home failed: {result.ReasonCode}.");
        }
    }

    public void RequestPause()
    {
        if (!fileApprenticeInteractions.RequestStop(pause: true))
        {
            viewModel.ReportAction(
                "No Tooltail-owned operation is active; no operation was paused.");
        }
    }

    public void RequestCancel()
    {
        if (!fileApprenticeInteractions.RequestStop(pause: false))
        {
            viewModel.ReportAction(
                "No Tooltail-owned operation is active; no operation was cancelled.");
        }
    }

    public Task RevokeFolderGrantAsync(CancellationToken cancellationToken = default) =>
        fileApprenticeInteractions.RevokeFolderGrantAsync(cancellationToken);
}
