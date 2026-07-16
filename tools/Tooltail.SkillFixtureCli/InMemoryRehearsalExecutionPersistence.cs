using Tooltail.Domain.Execution;
using Tooltail.Domain.Permissions;
using Tooltail.Features.FileSkills.Rehearsal;

namespace Tooltail.SkillFixtureCli;

internal sealed class InMemoryRehearsalExecutionPersistence :
    IRehearsalExecutionPersistence
{
    public ValueTask<RehearsalPersistenceResult> PrepareAsync(
        LocalFolderGrant temporaryGrant,
        ExecutionPlan plan,
        PlanApproval approval,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool valid = temporaryGrant.State == ResourceGrantState.Active &&
            approval.State == PlanApprovalState.Active &&
            approval.Purpose == PlanApprovalPurpose.Rehearsal &&
            plan.Definition.GrantId == temporaryGrant.Id &&
            approval.PlanId == plan.Definition.Id &&
            approval.Fingerprint == plan.Fingerprint;
        return ValueTask.FromResult(
            valid
                ? RehearsalPersistenceResult.Success("fixture.rehearsal_prepared")
                : RehearsalPersistenceResult.Failure("fixture.rehearsal_invalid"));
    }

    public ValueTask<RehearsalPersistenceResult> RetireGrantAsync(
        LocalFolderGrant temporaryGrant,
        DateTimeOffset retiredUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            temporaryGrant.State == ResourceGrantState.Active
                ? RehearsalPersistenceResult.Success("fixture.rehearsal_retired")
                : RehearsalPersistenceResult.Failure("fixture.rehearsal_retire_invalid"));
    }
}
