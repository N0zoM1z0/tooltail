using System.Text;
using Tooltail.Application.Abstractions;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Permissions;
using Tooltail.Features.FileSkills.Execution;

namespace Tooltail.Features.FileSkills.Rehearsal;

public sealed record RehearsalPersistenceResult(bool IsSuccess, string ReasonCode)
{
    public static RehearsalPersistenceResult Success(string reasonCode) =>
        new(true, reasonCode);

    public static RehearsalPersistenceResult Failure(string reasonCode) =>
        new(false, reasonCode);
}

public interface IRehearsalExecutionPersistence
{
    ValueTask<RehearsalPersistenceResult> PrepareAsync(
        LocalFolderGrant temporaryGrant,
        ExecutionPlan plan,
        PlanApproval approval,
        CancellationToken cancellationToken = default);

    ValueTask<RehearsalPersistenceResult> RetireGrantAsync(
        LocalFolderGrant temporaryGrant,
        DateTimeOffset retiredUtc,
        CancellationToken cancellationToken = default);
}

public sealed class FileSkillStateRehearsalExecutionPersistence :
    IRehearsalExecutionPersistence
{
    private const string RetirementReason = "rehearsal.completed";
    private readonly IFileSkillStateStore stateStore;

    public FileSkillStateRehearsalExecutionPersistence(IFileSkillStateStore stateStore)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        this.stateStore = stateStore;
    }

    public async ValueTask<RehearsalPersistenceResult> PrepareAsync(
        LocalFolderGrant temporaryGrant,
        ExecutionPlan plan,
        PlanApproval approval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(temporaryGrant);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(approval);
        if (approval.Purpose != PlanApprovalPurpose.Rehearsal ||
            approval.State != PlanApprovalState.Active ||
            plan.Definition.GrantId != temporaryGrant.Id ||
            plan.Definition.RootIdentity != temporaryGrant.RootIdentity ||
            approval.PlanId != plan.Definition.Id ||
            approval.Fingerprint != plan.Fingerprint ||
            !CanonicalExecutionPlan.HasValidFingerprint(plan))
        {
            return RehearsalPersistenceResult.Failure(
                "rehearsal.persistence_binding_invalid");
        }

        bool grantStored = false;
        try
        {
            StateWriteResult grant = await stateStore.StoreLocalFolderGrantAsync(
                new LocalFolderGrantStateRecord(
                    temporaryGrant,
                    ProtectedCanonicalRoot: null),
                cancellationToken).ConfigureAwait(false);
            if (!grant.IsSuccess)
            {
                return RehearsalPersistenceResult.Failure(grant.FailureCode!);
            }

            grantStored = true;
            StateWriteResult storedPlan = await stateStore.StoreExecutionPlanAsync(
                plan,
                Encoding.UTF8.GetString(CanonicalExecutionPlan.Encode(plan.Definition)),
                cancellationToken).ConfigureAwait(false);
            if (!storedPlan.IsSuccess)
            {
                RehearsalPersistenceResult retired =
                    await RetireAfterFailedPreparationAsync(
                        temporaryGrant,
                        plan.Definition.CreatedUtc).ConfigureAwait(false);
                return retired.IsSuccess
                    ? RehearsalPersistenceResult.Failure(storedPlan.FailureCode!)
                    : retired;
            }

            StateWriteResult storedApproval = await stateStore.StoreApprovalAsync(
                approval,
                cancellationToken).ConfigureAwait(false);
            if (!storedApproval.IsSuccess)
            {
                RehearsalPersistenceResult retired =
                    await RetireAfterFailedPreparationAsync(
                        temporaryGrant,
                        approval.ApprovedUtc).ConfigureAwait(false);
                return retired.IsSuccess
                    ? RehearsalPersistenceResult.Failure(storedApproval.FailureCode!)
                    : retired;
            }

            return RehearsalPersistenceResult.Success("rehearsal.persistence_prepared");
        }
        catch (OperationCanceledException)
        {
            if (grantStored)
            {
                RehearsalPersistenceResult retired =
                    await RetireAfterFailedPreparationAsync(
                        temporaryGrant,
                        plan.Definition.CreatedUtc).ConfigureAwait(false);
                if (!retired.IsSuccess)
                {
                    return retired;
                }
            }

            return RehearsalPersistenceResult.Failure("rehearsal.cancelled");
        }
    }

    public async ValueTask<RehearsalPersistenceResult> RetireGrantAsync(
        LocalFolderGrant temporaryGrant,
        DateTimeOffset retiredUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(temporaryGrant);
        var revoked = temporaryGrant.Revoke(retiredUtc, RetirementReason);
        if (!revoked.IsSuccess)
        {
            return RehearsalPersistenceResult.Failure(revoked.Error!.Code);
        }

        StateWriteResult stored = await stateStore.StoreLocalFolderGrantAsync(
            new LocalFolderGrantStateRecord(
                revoked.Value!,
                ProtectedCanonicalRoot: null),
            cancellationToken).ConfigureAwait(false);
        return stored.IsSuccess
            ? RehearsalPersistenceResult.Success("rehearsal.grant_retired")
            : RehearsalPersistenceResult.Failure(stored.FailureCode!);
    }

    private ValueTask<RehearsalPersistenceResult> RetireAfterFailedPreparationAsync(
        LocalFolderGrant temporaryGrant,
        DateTimeOffset retiredUtc) =>
        RetireGrantAsync(
            temporaryGrant,
            retiredUtc,
            CancellationToken.None);
}
