using Tooltail.Contracts.Research;
using Tooltail.Infrastructure.LocalResearch;

namespace Tooltail.Desktop.Presentation;

public sealed class ResearchInteractionController(
    LocalResearchStore store,
    ResearchStudyViewModel viewModel,
    ResearchEventRecorder recorder,
    FileApprenticeInteractionController fileApprentice) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await RunStatusActionAsync(
            "research.initializing",
            store.InitializeAsync,
            cancellationToken).ConfigureAwait(true);
    }

    public async Task EnableAsync(CancellationToken cancellationToken = default)
    {
        await RunStatusActionAsync(
            "research.enabling",
            store.EnableAsync,
            cancellationToken).ConfigureAwait(true);
    }

    public async Task PreviewAsync(CancellationToken cancellationToken = default)
    {
        if (!await TryEnterAsync("research.previewing", cancellationToken).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            viewModel.ApplyPreview(await store.PreviewAsync(cancellationToken)
                .ConfigureAwait(true));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ExportAsync(CancellationToken cancellationToken = default)
    {
        if (!await TryEnterAsync("research.exporting", cancellationToken).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            viewModel.ApplyExport(await store.ExportAsync(cancellationToken)
                .ConfigureAwait(true));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        await RunStatusActionAsync(
            "research.deleting",
            store.DeleteAllAsync,
            cancellationToken).ConfigureAwait(true);
    }

    public async Task ResetStudyFixtureAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await TryEnterAsync("research.resetting_session", cancellationToken)
                .ConfigureAwait(true))
        {
            return;
        }

        try
        {
            ResearchStoreStatus status = await store.ResetSessionAsync(cancellationToken)
                .ConfigureAwait(true);
            viewModel.ApplyStatus(status);
            if (!status.IsSuccess)
            {
                return;
            }

            await fileApprentice.RevokeFolderGrantAsync(cancellationToken)
                .ConfigureAwait(true);
            await fileApprentice.CreateSafeLabAsync(cancellationToken)
                .ConfigureAwait(true);
            viewModel.ApplyStatus(await store.InitializeAsync(cancellationToken)
                .ConfigureAwait(true));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SubmitRatingAsync(CancellationToken cancellationToken = default)
    {
        if (viewModel.SelectedRating is not int rating || rating is < 1 or > 7)
        {
            return;
        }

        DateTimeOffset started = recorder.StartTiming();
        await recorder.RecordAsync(
            ResearchEventType.RatingSubmitted,
            started,
            success: true,
            "research.rating_submitted",
            rating: rating).ConfigureAwait(true);
        viewModel.ClearSubmittedRating();
    }

    private async Task RunStatusActionAsync(
        string pendingReason,
        Func<CancellationToken, Task<ResearchStoreStatus>> action,
        CancellationToken cancellationToken)
    {
        if (!await TryEnterAsync(pendingReason, cancellationToken).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            viewModel.ApplyStatus(await action(cancellationToken).ConfigureAwait(true));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> TryEnterAsync(
        string pendingReason,
        CancellationToken cancellationToken)
    {
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(true))
        {
            return false;
        }

        viewModel.Begin(pendingReason);
        return true;
    }

    public void Dispose() => gate.Dispose();
}
