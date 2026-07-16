using Tooltail.Application.Diagnostics;

namespace Tooltail.Desktop.Presentation;

public sealed class DiagnosticExportController(
    DiagnosticExportWorkflowService workflow,
    DesktopCompanionSession companion,
    FileApprenticeViewModel fileApprentice,
    ResearchStudyViewModel research,
    DiagnosticExportViewModel viewModel)
{
    private DiagnosticPreviewWorkflowResult? pending;

    public async Task PreviewAsync(CancellationToken cancellationToken = default)
    {
        if (!viewModel.CanPreview || fileApprentice.IsBusy)
        {
            return;
        }

        viewModel.Begin(
            "diagnostic.previewing",
            "Reading only closed local counts, states, and reason codes…");
        try
        {
            DiagnosticPreviewWorkflowResult result = await workflow.PreviewAsync(
                companion.CompanionId,
                fileApprentice.CurrentBody,
                new DiagnosticResearchSnapshot(
                    research.IsEnabled,
                    research.EventCount,
                    research.EventBytes,
                    research.ReasonCode),
                cancellationToken).ConfigureAwait(true);
            pending = result.IsSuccess ? result : null;
            viewModel.ApplyPreview(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            pending = null;
            viewModel.ApplyPreview(new DiagnosticPreviewWorkflowResult(
                false,
                "diagnostic.preview_cancelled",
                string.Empty,
                0,
                null,
                null,
                null));
        }
    }

    public async Task ExportAsync(CancellationToken cancellationToken = default)
    {
        DiagnosticPreviewWorkflowResult? exact = pending;
        if (!viewModel.CanExport || fileApprentice.IsBusy || exact is null)
        {
            return;
        }

        viewModel.Begin(
            "diagnostic.exporting",
            "Writing the exact reviewed bytes once under Tooltail-owned local storage…");
        try
        {
            DiagnosticExportWorkflowResult result = await workflow.ExportAsync(
                exact,
                cancellationToken).ConfigureAwait(true);
            if (result.IsSuccess)
            {
                pending = null;
            }

            viewModel.ApplyExport(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            viewModel.ApplyExport(new DiagnosticExportWorkflowResult(
                false,
                "diagnostic.export_cancelled",
                null,
                0,
                null));
        }
    }
}
