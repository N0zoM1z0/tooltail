using System.Text;
using Tooltail.Application.Abstractions;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Execution;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Features.FileSkills.Presentation;
using Tooltail.Features.FileSkills.Rehearsal;
using Tooltail.Features.FileSkills.Skills;
using Tooltail.Features.FileSkills.Snapshots;

namespace Tooltail.Desktop.Presentation;

public sealed record ProductionExecutionWorkflowResult(
    bool IsSuccess,
    string ReasonCode,
    ExecutionPlan? Plan,
    PlanApproval? Approval,
    FileExecutionResult? Execution,
    SkillVersion? SkillVersion,
    SkillCardViewModel? Card);

public sealed class ProductionExecutionWorkflowService
{
    private static readonly TimeSpan UndoWindow = TimeSpan.FromDays(1);
    private readonly IFileSkillStateStore stateStore;
    private readonly IExecutionJournalStore journalStore;
    private readonly WindowsPathSafetyService pathSafety;
    private readonly FolderSnapshotService snapshotService;
    private readonly IFileMutationEngine mutationEngine;
    private readonly IClock clock;
    private readonly IIdGenerator idGenerator;

    public ProductionExecutionWorkflowService(
        IFileSkillStateStore stateStore,
        IExecutionJournalStore journalStore,
        WindowsPathSafetyService pathSafety,
        FolderSnapshotService snapshotService,
        IFileMutationEngine mutationEngine,
        IClock clock,
        IIdGenerator idGenerator)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(journalStore);
        ArgumentNullException.ThrowIfNull(pathSafety);
        ArgumentNullException.ThrowIfNull(snapshotService);
        ArgumentNullException.ThrowIfNull(mutationEngine);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(idGenerator);
        this.stateStore = stateStore;
        this.journalStore = journalStore;
        this.pathSafety = pathSafety;
        this.snapshotService = snapshotService;
        this.mutationEngine = mutationEngine;
        this.clock = clock;
        this.idGenerator = idGenerator;
    }

    public async Task<ProductionExecutionWorkflowResult> ApproveAndExecuteAsync(
        SafeLabGrantResult lab,
        SkillCompilationWorkflowResult compilation,
        SkillRehearsalWorkflowResult rehearsal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lab);
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(rehearsal);
        if (!lab.IsSuccess || lab.Grant is null || lab.Root is null ||
            !compilation.IsSuccess ||
            compilation.Specification is null ||
            compilation.CardRequest is null ||
            !rehearsal.IsSuccess ||
            rehearsal.Rehearsal?.IsPassed != true ||
            rehearsal.ProductionPlan is null)
        {
            return Failure("execution.production_request_invalid");
        }

        ExecutionPlan plan = rehearsal.ProductionPlan;
        DateTimeOffset approvedUtc = clock.UtcNow;
        if (approvedUtc.Offset != TimeSpan.Zero ||
            approvedUtc < plan.Definition.CreatedUtc ||
            approvedUtc >= plan.Definition.ExpiresUtc ||
            plan.Definition.GrantId != lab.Grant.Id ||
            plan.Definition.RootIdentity != lab.Root.Identity)
        {
            return Failure("execution.production_plan_inactive", plan);
        }

        StateReadResult<StoredPlanDocument> storedPlan =
            await stateStore.LoadPlanDocumentAsync(
                plan.Definition.Id,
                cancellationToken).ConfigureAwait(false);
        string canonical = Encoding.UTF8.GetString(
            CanonicalExecutionPlan.Encode(plan.Definition));
        if (!storedPlan.IsSuccess ||
            storedPlan.Value!.Fingerprint != plan.Fingerprint ||
            !string.Equals(storedPlan.Value.CanonicalJson, canonical, StringComparison.Ordinal))
        {
            return Failure(
                storedPlan.IsSuccess
                    ? "execution.production_plan_readback_mismatch"
                    : storedPlan.ReasonCode,
                plan);
        }

        StateReadResult<SkillVersionStateRecord> storedSkill =
            await stateStore.LoadSkillVersionAsync(
                plan.Definition.SkillId,
                plan.Definition.SkillVersion,
                cancellationToken).ConfigureAwait(false);
        if (!storedSkill.IsSuccess ||
            storedSkill.Value!.Version.Lifecycle != SkillLifecycleState.Draft ||
            !string.Equals(
                storedSkill.Value.Version.SpecificationHash,
                plan.Definition.SkillSpecificationHash.Value,
                StringComparison.Ordinal))
        {
            return Failure(
                storedSkill.IsSuccess
                    ? "execution.production_skill_not_draft"
                    : storedSkill.ReasonCode,
                plan);
        }

        var transitioned = storedSkill.Value.Version.TransitionTo(
            SkillLifecycleState.Approved);
        if (!transitioned.IsSuccess)
        {
            return Failure(transitioned.Error!.Code, plan);
        }

        SkillVersion approvedVersion = transitioned.Value!;
        StateWriteResult storedApproved = await stateStore.StoreSkillVersionAsync(
            storedSkill.Value with
            {
                Version = approvedVersion,
                ApprovedUtc = approvedUtc,
            },
            cancellationToken).ConfigureAwait(false);
        if (!storedApproved.IsSuccess)
        {
            return Failure(storedApproved.FailureCode!, plan);
        }

        PlanApproval activeApproval = PlanApproval.Issue(
            new ApprovalId(idGenerator.NewId()),
            plan,
            approvedUtc,
            plan.Definition.ExpiresUtc);
        StateWriteResult storedApproval = await stateStore.StoreApprovalAsync(
            activeApproval,
            cancellationToken).ConfigureAwait(false);
        if (!storedApproval.IsSuccess)
        {
            return Failure(
                storedApproval.FailureCode!,
                plan,
                activeApproval,
                approvedVersion);
        }

        PermissionGateway gateway = new(clock);
        var authorization = gateway.Authorize(
            plan,
            approvedVersion,
            lab.Grant,
            activeApproval);
        if (!authorization.IsSuccess)
        {
            StateWriteResult revoked = await RevokeUnusedApprovalAsync(
                activeApproval,
                "production.authorization_failed").ConfigureAwait(false);
            return Failure(
                revoked.IsSuccess
                    ? authorization.Error!.Code
                    : revoked.FailureCode!,
                plan,
                activeApproval,
                approvedVersion);
        }

        FileSkillExecutor executor = new(
            clock,
            new FileSkillStateExecutionAuthoritySource(stateStore),
            journalStore,
            pathSafety,
            snapshotService,
            mutationEngine);
        FileExecutionResult execution = await executor.ExecuteAsync(
            new FileExecutionRequest(
                new ExecutionId(idGenerator.NewId()),
                new ReceiptId(idGenerator.NewId()),
                plan,
                authorization.Value!,
                lab.Root,
                FileExecutionMode.Production,
                UndoWindow),
            cancellationToken).ConfigureAwait(false);
        if (!execution.IsVerified && execution.Journal is null)
        {
            StateWriteResult revoked = await RevokeUnusedApprovalAsync(
                activeApproval,
                "production.execution_not_opened").ConfigureAwait(false);
            if (!revoked.IsSuccess)
            {
                return new ProductionExecutionWorkflowResult(
                    false,
                    revoked.FailureCode!,
                    plan,
                    activeApproval,
                    execution,
                    approvedVersion,
                    null);
            }
        }

        if (!execution.IsVerified)
        {
            return new ProductionExecutionWorkflowResult(
                false,
                execution.ReasonCode,
                plan,
                authorization.Value!.ConsumedApproval,
                execution,
                approvedVersion,
                BuildCard(
                    compilation.CardRequest,
                    rehearsal,
                    SkillLifecycleState.Approved,
                    SkillCardEvidenceKind.VerificationFailed,
                    execution.ReasonCode,
                    clock.UtcNow,
                    plan.Fingerprint));
        }

        var practiced = approvedVersion.TransitionTo(SkillLifecycleState.Practiced);
        if (!practiced.IsSuccess)
        {
            return new ProductionExecutionWorkflowResult(
                false,
                practiced.Error!.Code,
                plan,
                authorization.Value!.ConsumedApproval,
                execution,
                approvedVersion,
                null);
        }

        StateWriteResult storedPracticed = await stateStore.StoreSkillVersionAsync(
            storedSkill.Value with
            {
                Version = practiced.Value!,
                ApprovedUtc = approvedUtc,
            },
            CancellationToken.None).ConfigureAwait(false);
        SkillCardViewModel card = BuildCard(
            compilation.CardRequest,
            rehearsal,
            storedPracticed.IsSuccess
                ? SkillLifecycleState.Practiced
                : SkillLifecycleState.Approved,
            SkillCardEvidenceKind.VerifiedRun,
            execution.ReasonCode,
            execution.Receipt!.CompletedUtc,
            plan.Fingerprint);
        return new ProductionExecutionWorkflowResult(
            storedPracticed.IsSuccess,
            storedPracticed.IsSuccess
                ? "execution.production_verified"
                : storedPracticed.FailureCode!,
            plan,
            authorization.Value!.ConsumedApproval,
            execution,
            storedPracticed.IsSuccess ? practiced.Value : approvedVersion,
            card);
    }

    private static SkillCardViewModel BuildCard(
        SkillCardRequest original,
        SkillRehearsalWorkflowResult rehearsal,
        SkillLifecycleState lifecycle,
        SkillCardEvidenceKind executionKind,
        string executionReason,
        DateTimeOffset executionUtc,
        PlanFingerprint productionFingerprint)
    {
        SkillSpecificationHash hash = CanonicalSkillSpec.ComputeHash(
            original.Specification);
        SkillCardEvidence rehearsalEvidence = new(
            SkillCardEvidenceKind.RehearsalPassed,
            rehearsal.Rehearsal!.ReasonCode,
            rehearsal.Rehearsal.CompletedUtc,
            hash,
            rehearsal.Rehearsal.PlanFingerprint);
        SkillCardEvidence executionEvidence = new(
            executionKind,
            executionReason,
            executionUtc,
            hash,
            productionFingerprint);
        return SkillCardBuilder.Build(
            new SkillCardRequest(
                original.Specification,
                lifecycle,
                original.GrantedRootLabel,
                original.GrantedCapabilities,
                original.Samples,
                original.Evidence.Append(rehearsalEvidence).Append(executionEvidence),
                original.ParentSpecification,
                original.IsDisabled,
                original.CanDeleteLocalHistory));
    }

    private static ProductionExecutionWorkflowResult Failure(
        string reasonCode,
        ExecutionPlan? plan = null,
        PlanApproval? approval = null,
        SkillVersion? skillVersion = null) =>
        new(false, reasonCode, plan, approval, null, skillVersion, null);

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
}
