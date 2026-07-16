using Tooltail.Infrastructure.LocalResearch;
using Tooltail.Infrastructure.Sqlite;

namespace Tooltail.Desktop.Presentation;

public sealed class LocalDataLifecycleController(
    LocalStateDeletionService deletion,
    LocalResearchStore researchStore,
    ResearchStudyViewModel researchViewModel,
    FileApprenticeInteractionController fileApprentice,
    FileApprenticeViewModel fileApprenticeViewModel,
    LocalDataLifecycleViewModel viewModel)
{
    public void PrepareDeletion() => viewModel.ApplyPreview(deletion.Prepare());

    public async Task<LocalStateDeletionResult> DeleteAsync(
        CancellationToken cancellationToken = default)
    {
        if (!viewModel.TryBeginDeletion(out Guid requestId))
        {
            return Failure("local_state.delete_confirmation_required");
        }

        if (fileApprentice.HasActiveOperation || fileApprenticeViewModel.CanStopTeaching)
        {
            const string reason = "local_state.delete_work_active";
            viewModel.ApplyFailure(
                reason,
                "Stop teaching or cancel the active Tooltail-owned operation before deleting local state.");
            return Failure(reason);
        }

        try
        {
            if (fileApprenticeViewModel.CanRevokeFolderGrant)
            {
                await fileApprentice.RevokeFolderGrantAsync(cancellationToken)
                    .ConfigureAwait(true);
                if (fileApprenticeViewModel.CanRevokeFolderGrant)
                {
                    const string reason = "local_state.delete_grant_revoke_failed";
                    viewModel.ApplyFailure(
                        reason,
                        "The current folder grant could not be durably revoked; local state was retained.");
                    return Failure(reason);
                }
            }

            ResearchStoreStatus research = await researchStore.DeleteAllAsync(
                cancellationToken).ConfigureAwait(true);
            researchViewModel.ApplyStatus(research);
            if (!research.IsSuccess)
            {
                const string reason = "local_state.delete_research_failed";
                viewModel.ApplyFailure(
                    reason,
                    "Separate local research data could not be deleted; product state was retained for inspection.");
                return Failure(reason);
            }

            LocalStateDeletionResult result = await deletion.DeleteAsync(
                requestId,
                cancellationToken: cancellationToken).ConfigureAwait(true);
            viewModel.ApplyResult(result);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            const string reason = "local_state.delete_cancelled";
            viewModel.ApplyFailure(reason, "Local state deletion was cancelled before intent.");
            return Failure(reason);
        }
    }

    private static LocalStateDeletionResult Failure(string reasonCode) =>
        new(
            false,
            reasonCode,
            RequiresShutdown: false,
            RequiresRecovery: false);
}
