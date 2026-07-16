using System.Text;
using Tooltail.Application.Abstractions;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Execution;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Features.FileSkills.Snapshots;
using Tooltail.Features.FileSkills.Undo;

namespace Tooltail.Desktop.Presentation;

public sealed record UndoPlanningWorkflowResult(
    bool IsSuccess,
    string ReasonCode,
    RecoveryPlan? Plan,
    ExecutionPlan? OriginalPlan,
    ExecutionJournal? OriginalJournal,
    ExecutionReceipt? OriginalReceipt,
    int OperationCount);

public sealed record UndoExecutionWorkflowResult(
    bool IsSuccess,
    string ReasonCode,
    RecoveryPlan? Plan,
    PlanApproval? Approval,
    UndoExecutionResult? Execution);

public sealed class UndoWorkflowService
{
    private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(10);
    private readonly IFileSkillStateStore stateStore;
    private readonly IExecutionJournalStore journalStore;
    private readonly IExecutionJournalReader journalReader;
    private readonly WindowsPathSafetyService pathSafety;
    private readonly FolderSnapshotService snapshotService;
    private readonly IClock clock;
    private readonly IIdGenerator idGenerator;
    private readonly UndoPlanner planner = new();

    public UndoWorkflowService(
        IFileSkillStateStore stateStore,
        IExecutionJournalStore journalStore,
        IExecutionJournalReader journalReader,
        WindowsPathSafetyService pathSafety,
        FolderSnapshotService snapshotService,
        IClock clock,
        IIdGenerator idGenerator)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(journalStore);
        ArgumentNullException.ThrowIfNull(journalReader);
        ArgumentNullException.ThrowIfNull(pathSafety);
        ArgumentNullException.ThrowIfNull(snapshotService);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(idGenerator);
        this.stateStore = stateStore;
        this.journalStore = journalStore;
        this.journalReader = journalReader;
        this.pathSafety = pathSafety;
        this.snapshotService = snapshotService;
        this.clock = clock;
        this.idGenerator = idGenerator;
    }

    public async Task<UndoPlanningWorkflowResult> PlanAsync(
        SafeLabGrantResult lab,
        ProductionExecutionWorkflowResult production,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lab);
        ArgumentNullException.ThrowIfNull(production);
        if (!lab.IsSuccess || lab.Grant is null || lab.Root is null ||
            !production.IsSuccess ||
            production.Plan is null ||
            production.Execution?.IsVerified != true)
        {
            return PlanningFailure("undo.desktop_request_invalid");
        }

        ExecutionId originalExecutionId =
            production.Execution.Receipt!.ExecutionId;
        ExecutionJournalReadResult journalRead = await journalReader.LoadJournalAsync(
            originalExecutionId,
            cancellationToken).ConfigureAwait(false);
        ExecutionReceiptReadResult receiptRead = await journalReader.LoadReceiptAsync(
            originalExecutionId,
            cancellationToken).ConfigureAwait(false);
        if (!journalRead.IsSuccess ||
            !receiptRead.IsSuccess ||
            receiptRead.Kind != PersistedReceiptKind.Standard)
        {
            return PlanningFailure(
                !journalRead.IsSuccess ? journalRead.ReasonCode : receiptRead.ReasonCode);
        }

        StateReadResult<SkillVersionStateRecord> storedSkill =
            await stateStore.LoadSkillVersionAsync(
                production.Plan.Definition.SkillId,
                production.Plan.Definition.SkillVersion,
                cancellationToken).ConfigureAwait(false);
        if (!storedSkill.IsSuccess)
        {
            return PlanningFailure(storedSkill.ReasonCode);
        }

        FolderSnapshot current = await snapshotService.CaptureAsync(
            lab.Root,
            lab.Grant,
            cancellationToken).ConfigureAwait(false);
        if (!current.IsComplete)
        {
            return PlanningFailure(
                current.ReasonCode ?? "undo.current_snapshot_failed");
        }

        DateTimeOffset createdUtc = clock.UtcNow;
        DateTimeOffset expiresUtc = createdUtc + PlanLifetime;
        if (receiptRead.StandardReceipt!.UndoAvailableUntilUtc is not null &&
            expiresUtc > receiptRead.StandardReceipt.UndoAvailableUntilUtc.Value)
        {
            expiresUtc = receiptRead.StandardReceipt.UndoAvailableUntilUtc.Value;
        }

        if (lab.Grant.ExpiresAt is not null && expiresUtc > lab.Grant.ExpiresAt.Value)
        {
            expiresUtc = lab.Grant.ExpiresAt.Value;
        }

        UndoPlanningResult planned = planner.Plan(
            new UndoPlanningRequest(
                new PlanId(idGenerator.NewId()),
                production.Plan,
                journalRead.Journal!,
                receiptRead.StandardReceipt,
                storedSkill.Value!.Version,
                lab.Grant,
                current,
                createdUtc,
                expiresUtc));
        if (!planned.IsReady)
        {
            return PlanningFailure(planned.ReasonCode);
        }

        RecoveryPlan recoveryPlan = planned.Plan!;
        StateWriteResult storedPlan = await stateStore.StoreRecoveryPlanAsync(
            recoveryPlan,
            Encoding.UTF8.GetString(
                CanonicalRecoveryPlan.Encode(recoveryPlan.Definition)),
            cancellationToken).ConfigureAwait(false);
        return storedPlan.IsSuccess
            ? new UndoPlanningWorkflowResult(
                true,
                "undo.preview_ready",
                recoveryPlan,
                production.Plan,
                journalRead.Journal,
                receiptRead.StandardReceipt,
                planned.RecoveryOperationCount)
            : PlanningFailure(storedPlan.FailureCode!);
    }

    public async Task<UndoExecutionWorkflowResult> ApproveAndExecuteAsync(
        SafeLabGrantResult lab,
        UndoPlanningWorkflowResult preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lab);
        ArgumentNullException.ThrowIfNull(preview);
        if (!lab.IsSuccess || lab.Grant is null || lab.Root is null ||
            !preview.IsSuccess ||
            preview.Plan is null ||
            preview.OriginalPlan is null ||
            preview.OriginalJournal is null ||
            preview.OriginalReceipt is null)
        {
            return ExecutionFailure("undo.desktop_request_invalid");
        }

        RecoveryPlan plan = preview.Plan;
        DateTimeOffset approvedUtc = clock.UtcNow;
        if (approvedUtc.Offset != TimeSpan.Zero ||
            approvedUtc < plan.Definition.CreatedUtc ||
            approvedUtc >= plan.Definition.ExpiresUtc ||
            plan.Definition.GrantId != lab.Grant.Id ||
            plan.Definition.RootIdentity != lab.Root.Identity)
        {
            return ExecutionFailure("undo.preview_inactive", plan);
        }

        StateReadResult<StoredPlanDocument> storedPlan =
            await stateStore.LoadPlanDocumentAsync(
                plan.Definition.Id,
                cancellationToken).ConfigureAwait(false);
        string canonical = Encoding.UTF8.GetString(
            CanonicalRecoveryPlan.Encode(plan.Definition));
        if (!storedPlan.IsSuccess ||
            storedPlan.Value!.Kind != PersistedPlanKind.Recovery ||
            storedPlan.Value.Fingerprint != plan.Fingerprint ||
            !string.Equals(storedPlan.Value.CanonicalJson, canonical, StringComparison.Ordinal))
        {
            return ExecutionFailure(
                storedPlan.IsSuccess
                    ? "undo.preview_readback_mismatch"
                    : storedPlan.ReasonCode,
                plan);
        }

        ExecutionJournalReadResult originalJournal =
            await journalReader.LoadJournalAsync(
                plan.Definition.OriginalExecutionId,
                cancellationToken).ConfigureAwait(false);
        ExecutionReceiptReadResult originalReceipt =
            await journalReader.LoadReceiptAsync(
                plan.Definition.OriginalExecutionId,
                cancellationToken).ConfigureAwait(false);
        StateReadResult<SkillVersionStateRecord> storedSkill =
            await stateStore.LoadSkillVersionAsync(
                plan.Definition.SkillId,
                plan.Definition.SkillVersion,
                cancellationToken).ConfigureAwait(false);
        if (!originalJournal.IsSuccess ||
            !originalReceipt.IsSuccess ||
            originalReceipt.Kind != PersistedReceiptKind.Standard ||
            !storedSkill.IsSuccess)
        {
            return ExecutionFailure("undo.evidence_readback_failed", plan);
        }

        PlanApproval activeApproval = PlanApproval.IssueUndo(
            new ApprovalId(idGenerator.NewId()),
            plan,
            approvedUtc,
            plan.Definition.ExpiresUtc);
        StateWriteResult storedApproval = await stateStore.StoreApprovalAsync(
            activeApproval,
            cancellationToken).ConfigureAwait(false);
        if (!storedApproval.IsSuccess)
        {
            return ExecutionFailure(
                storedApproval.FailureCode!,
                plan,
                activeApproval);
        }

        PermissionGateway gateway = new(clock);
        var authorization = gateway.AuthorizeUndo(
            plan,
            storedSkill.Value!.Version,
            lab.Grant,
            activeApproval);
        if (!authorization.IsSuccess)
        {
            StateWriteResult revoked = await RevokeUnusedApprovalAsync(
                activeApproval,
                "undo.authorization_failed").ConfigureAwait(false);
            return ExecutionFailure(
                revoked.IsSuccess ? authorization.Error!.Code : revoked.FailureCode!,
                plan,
                activeApproval);
        }

        FileSkillExecutor executor = new(
            clock,
            new FileSkillStateExecutionAuthoritySource(stateStore),
            journalStore,
            pathSafety,
            snapshotService);
        UndoExecutionResult execution = await executor.ExecuteUndoAsync(
            new UndoExecutionRequest(
                new ExecutionId(idGenerator.NewId()),
                new ReceiptId(idGenerator.NewId()),
                plan,
                authorization.Value!,
                preview.OriginalPlan,
                originalJournal.Journal!,
                originalReceipt.StandardReceipt!,
                lab.Root),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!execution.IsVerified && execution.RecoveryJournal is null)
        {
            StateWriteResult revoked = await RevokeUnusedApprovalAsync(
                activeApproval,
                "undo.execution_not_opened").ConfigureAwait(false);
            if (!revoked.IsSuccess)
            {
                return ExecutionFailure(
                    revoked.FailureCode!,
                    plan,
                    activeApproval,
                    execution);
            }
        }

        return new UndoExecutionWorkflowResult(
            execution.IsVerified,
            execution.IsVerified ? "undo.production_restored" : execution.ReasonCode,
            plan,
            authorization.Value!.ConsumedApproval,
            execution);
    }

    private async ValueTask<StateWriteResult> RevokeUnusedApprovalAsync(
        PlanApproval approval,
        string reasonCode)
    {
        var revoked = approval.Revoke(clock.UtcNow, reasonCode);
        return revoked.IsSuccess
            ? await stateStore.StoreApprovalAsync(
                revoked.Value!,
                CancellationToken.None).ConfigureAwait(false)
            : StateWriteResult.Failure(revoked.Error!.Code);
    }

    private static UndoPlanningWorkflowResult PlanningFailure(string reasonCode) =>
        new(false, reasonCode, null, null, null, null, 0);

    private static UndoExecutionWorkflowResult ExecutionFailure(
        string reasonCode,
        RecoveryPlan? plan = null,
        PlanApproval? approval = null,
        UndoExecutionResult? execution = null) =>
        new(false, reasonCode, plan, approval, execution);
}
