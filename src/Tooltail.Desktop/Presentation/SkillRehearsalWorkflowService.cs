using System.IO;
using System.Text;
using Tooltail.Application.Abstractions;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Features.FileSkills.Execution;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Features.FileSkills.Planning;
using Tooltail.Features.FileSkills.Rehearsal;
using Tooltail.Features.FileSkills.Skills;
using Tooltail.Features.FileSkills.Snapshots;
using Tooltail.Infrastructure.Sqlite;

namespace Tooltail.Desktop.Presentation;

public sealed record SkillRehearsalWorkflowResult(
    bool IsSuccess,
    string ReasonCode,
    SkillRehearsalResult? Rehearsal,
    ExecutionPlan? ProductionPlan,
    int MatchedFileCount);

public sealed class SkillRehearsalWorkflowService
{
    private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(10);
    private readonly TooltailSqliteDatabase database;
    private readonly IFileSkillStateStore stateStore;
    private readonly IExecutionJournalStore journalStore;
    private readonly WindowsPathSafetyService pathSafety;
    private readonly FolderSnapshotService snapshotService;
    private readonly IClock clock;
    private readonly IIdGenerator idGenerator;
    private readonly SkillPlanner planner = new();

    public SkillRehearsalWorkflowService(
        TooltailSqliteDatabase database,
        IFileSkillStateStore stateStore,
        IExecutionJournalStore journalStore,
        WindowsPathSafetyService pathSafety,
        FolderSnapshotService snapshotService,
        IClock clock,
        IIdGenerator idGenerator)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(journalStore);
        ArgumentNullException.ThrowIfNull(pathSafety);
        ArgumentNullException.ThrowIfNull(snapshotService);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(idGenerator);
        this.database = database;
        this.stateStore = stateStore;
        this.journalStore = journalStore;
        this.pathSafety = pathSafety;
        this.snapshotService = snapshotService;
        this.clock = clock;
        this.idGenerator = idGenerator;
    }

    public async Task<SkillRehearsalWorkflowResult> RehearseAsync(
        SafeLabGrantResult lab,
        SkillCompilationWorkflowResult compilation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lab);
        ArgumentNullException.ThrowIfNull(compilation);
        if (!lab.IsSuccess || lab.Grant is null || lab.Root is null ||
            !compilation.IsSuccess ||
            compilation.Specification is null)
        {
            return Failure("rehearsal.desktop_request_invalid");
        }

        StateReadResult<Tooltail.Application.Abstractions.SkillVersionStateRecord> stored =
            await stateStore.LoadSkillVersionAsync(
                new SkillId(compilation.Specification.SkillId),
                new Tooltail.Domain.Skills.SkillVersionNumber(
                    compilation.Specification.Version),
                cancellationToken).ConfigureAwait(false);
        if (!stored.IsSuccess)
        {
            return Failure(stored.ReasonCode);
        }

        SkillSpecificationHash specificationHash =
            CanonicalSkillSpec.ComputeHash(compilation.Specification);
        if (!string.Equals(
                stored.Value!.Version.SpecificationHash,
                specificationHash.Value,
                StringComparison.Ordinal))
        {
            return Failure("rehearsal.persisted_skill_mismatch");
        }

        PathSafetyResult<CanonicalLocalRoot> temporaryRoot =
            EnsureOwnedTemporaryRoot();
        if (!temporaryRoot.IsSuccess)
        {
            return Failure(temporaryRoot.Error!.Code);
        }

        TooltailOwnedRehearsalWorkspaceFactory workspaceFactory = new(
            temporaryRoot.Value!,
            pathSafety,
            idGenerator);
        SkillRehearsalService service = new(
            clock,
            workspaceFactory,
            journalStore,
            new FileSkillStateRehearsalExecutionPersistence(stateStore),
            pathSafety,
            snapshotService,
            planner);
        SkillRehearsalResult rehearsal = await service.RehearseAsync(
            new SkillRehearsalRequest(
                compilation.Specification,
                stored.Value.Version,
                lab.Root,
                lab.Grant,
                new GrantId(idGenerator.NewId()),
                new PlanId(idGenerator.NewId()),
                new ApprovalId(idGenerator.NewId()),
                new ExecutionId(idGenerator.NewId()),
                new ReceiptId(idGenerator.NewId()),
                PlanLifetime),
            cancellationToken).ConfigureAwait(false);
        if (!rehearsal.IsPassed)
        {
            return new SkillRehearsalWorkflowResult(
                false,
                rehearsal.ReasonCode,
                rehearsal,
                null,
                0);
        }

        FolderSnapshot current = await snapshotService.CaptureAsync(
            lab.Root,
            lab.Grant,
            cancellationToken).ConfigureAwait(false);
        if (!current.IsComplete)
        {
            return new SkillRehearsalWorkflowResult(
                false,
                current.ReasonCode ?? "rehearsal.production_snapshot_failed",
                rehearsal,
                null,
                0);
        }

        DateTimeOffset plannedUtc = clock.UtcNow;
        SkillPlanningResult planning = planner.DryRun(
            new SkillPlanningRequest(
                new PlanId(idGenerator.NewId()),
                compilation.Specification,
                specificationHash,
                lab.Grant,
                current,
                plannedUtc,
                plannedUtc + PlanLifetime));
        if (planning.Status != SkillPlanningStatus.Ready)
        {
            return new SkillRehearsalWorkflowResult(
                false,
                planning.Diagnostics.Count == 0
                    ? "rehearsal.production_planning_failed"
                    : planning.Diagnostics[0].Code,
                rehearsal,
                null,
                planning.MatchedFileCount);
        }

        ExecutionPlan productionPlan = planning.Plan!;
        StateWriteResult persisted = await stateStore.StoreExecutionPlanAsync(
            productionPlan,
            Encoding.UTF8.GetString(
                CanonicalExecutionPlan.Encode(productionPlan.Definition)),
            cancellationToken).ConfigureAwait(false);
        if (persisted.IsSuccess)
        {
            return new SkillRehearsalWorkflowResult(
                true,
                "rehearsal.production_plan_ready",
                rehearsal,
                productionPlan,
                planning.MatchedFileCount);
        }

        return new SkillRehearsalWorkflowResult(
            false,
            persisted.FailureCode!,
            rehearsal,
            null,
            planning.MatchedFileCount);
    }

    private PathSafetyResult<CanonicalLocalRoot> EnsureOwnedTemporaryRoot()
    {
        string? stateDirectory = Path.GetDirectoryName(database.DatabasePath);
        string? applicationRootPath = stateDirectory is null
            ? null
            : Path.GetDirectoryName(stateDirectory);
        PathSafetyResult<CanonicalLocalRoot> applicationRoot =
            pathSafety.CaptureRoot(applicationRootPath);
        if (!applicationRoot.IsSuccess)
        {
            return applicationRoot;
        }

        PathSafetyResult<BoundLocalPath> existing = pathSafety.Bind(
            applicationRoot.Value!,
            "Rehearsals",
            PathEntryExpectation.MayExist);
        if (!existing.IsSuccess)
        {
            return PathSafetyResult.Failure<CanonicalLocalRoot>(
                existing.Error!.Code,
                existing.Error.Message);
        }

        if (existing.Value!.Components[^1].Existed)
        {
            return pathSafety.CaptureSubroot(applicationRoot.Value!, "Rehearsals");
        }

        PathSafetyResult<BoundLocalPath> current = pathSafety.Revalidate(existing.Value);
        if (!current.IsSuccess)
        {
            return PathSafetyResult.Failure<CanonicalLocalRoot>(
                current.Error!.Code,
                current.Error.Message);
        }

        Directory.CreateDirectory(current.Value!.FullPath);
        return pathSafety.CaptureSubroot(applicationRoot.Value!, "Rehearsals");
    }

    private static SkillRehearsalWorkflowResult Failure(string reasonCode) =>
        new(false, reasonCode, null, null, 0);
}
