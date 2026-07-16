using System.IO;
using Tooltail.Application.FileSkills;

namespace Tooltail.Desktop.Presentation;

public sealed class FileApprenticeInteractionController
{
    private readonly FileApprenticeStartupService startupService;
    private readonly DesktopCompanionSession companionSession;
    private readonly FileApprenticeViewModel viewModel;
    private readonly SafeLabGrantService safeLab;
    private readonly TeachingWorkflowService teaching;
    private readonly SkillCompilationWorkflowService compiler;
    private readonly SkillRehearsalWorkflowService rehearsal;
    private readonly ProductionExecutionWorkflowService production;
    private readonly UndoWorkflowService undo;
    private readonly SkillCorrectionWorkflowService correction;
    private readonly CapsuleExportWorkflowService capsuleExport;
    private readonly object gate = new();
    private Task? initializationTask;
    private SafeLabGrantResult? activeLab;
    private TeachingWorkflowResult? latestTeaching;
    private SkillCompilationWorkflowResult? latestCompilation;
    private SkillRehearsalWorkflowResult? latestRehearsal;
    private ProductionExecutionWorkflowResult? latestProduction;
    private UndoPlanningWorkflowResult? latestUndoPreview;

    public FileApprenticeInteractionController(
        FileApprenticeStartupService startupService,
        DesktopCompanionSession companionSession,
        FileApprenticeViewModel viewModel,
        SafeLabGrantService safeLab,
        TeachingWorkflowService teaching,
        SkillCompilationWorkflowService compiler,
        SkillRehearsalWorkflowService rehearsal,
        ProductionExecutionWorkflowService production,
        UndoWorkflowService undo,
        SkillCorrectionWorkflowService correction,
        CapsuleExportWorkflowService capsuleExport)
    {
        ArgumentNullException.ThrowIfNull(startupService);
        ArgumentNullException.ThrowIfNull(companionSession);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(safeLab);
        ArgumentNullException.ThrowIfNull(teaching);
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(rehearsal);
        ArgumentNullException.ThrowIfNull(production);
        ArgumentNullException.ThrowIfNull(undo);
        ArgumentNullException.ThrowIfNull(correction);
        ArgumentNullException.ThrowIfNull(capsuleExport);
        this.startupService = startupService;
        this.companionSession = companionSession;
        this.viewModel = viewModel;
        this.safeLab = safeLab;
        this.teaching = teaching;
        this.compiler = compiler;
        this.rehearsal = rehearsal;
        this.production = production;
        this.undo = undo;
        this.correction = correction;
        this.capsuleExport = capsuleExport;
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
                        activeLab = restored;
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
                activeLab = result;
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

    public async Task StartTeachingAsync(CancellationToken cancellationToken = default)
    {
        if (!viewModel.CanStartTeaching || activeLab is null)
        {
            return;
        }

        viewModel.BeginAction("Capturing the authoritative baseline before observation…");
        TeachingWorkflowResult result = await teaching.StartAsync(activeLab, cancellationToken);
        viewModel.ApplyTeachingStart(result);
    }

    public async Task StopTeachingAsync(CancellationToken cancellationToken = default)
    {
        if (!viewModel.CanStopTeaching)
        {
            return;
        }

        viewModel.BeginAction("Stopping watcher hints and capturing the authoritative final snapshot…");
        TeachingWorkflowResult result = await teaching.StopAsync(cancellationToken);
        latestTeaching = result;
        viewModel.ApplyTeachingStop(result);
    }

    public async Task CompileSkillAsync(CancellationToken cancellationToken = default)
    {
        if (!viewModel.CanCompileSkill || activeLab is null || latestTeaching is null)
        {
            return;
        }

        viewModel.BeginAction("Running the deterministic compiler over exact normalized examples…");
        SkillCompilationWorkflowResult result = await compiler.CompileAsync(
            activeLab,
            latestTeaching,
            viewModel.CompilerAnswers(),
            cancellationToken);
        if (result.IsSuccess)
        {
            latestCompilation = result;
        }

        viewModel.ApplyCompilation(result);
    }

    public async Task RehearseSkillAsync(CancellationToken cancellationToken = default)
    {
        if (!viewModel.CanRehearseSkill ||
            activeLab is null ||
            latestCompilation is null)
        {
            return;
        }

        viewModel.BeginAction(
            "Copying bounded fixtures into a Tooltail-owned root for shared-executor rehearsal…");
        SkillRehearsalWorkflowResult result = await rehearsal.RehearseAsync(
            activeLab,
            latestCompilation,
            cancellationToken);
        if (result.IsSuccess)
        {
            latestRehearsal = result;
        }

        viewModel.ApplyRehearsal(result);
    }

    public async Task ApproveAndExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (!viewModel.CanApproveAndExecute ||
            activeLab is null ||
            latestCompilation is null ||
            latestRehearsal is null)
        {
            return;
        }

        viewModel.BeginAction(
            "Binding one production approval to the displayed canonical fingerprint…");
        ProductionExecutionWorkflowResult result =
            await production.ApproveAndExecuteAsync(
                activeLab,
                latestCompilation,
                latestRehearsal,
                cancellationToken);
        if (result.IsSuccess)
        {
            latestProduction = result;
        }

        viewModel.ApplyProductionExecution(result);
    }

    public async Task PlanUndoAsync(CancellationToken cancellationToken = default)
    {
        if (!viewModel.CanPlanUndo || activeLab is null || latestProduction is null)
        {
            return;
        }

        viewModel.BeginAction(
            "Reloading the verified receipt and current snapshot to derive an exact recovery preview…");
        UndoPlanningWorkflowResult result = await undo.PlanAsync(
            activeLab,
            latestProduction,
            cancellationToken);
        if (result.IsSuccess)
        {
            latestUndoPreview = result;
        }

        viewModel.ApplyUndoPlanning(result);
    }

    public async Task ApproveAndExecuteUndoAsync(
        CancellationToken cancellationToken = default)
    {
        if (!viewModel.CanApproveUndo ||
            activeLab is null ||
            latestUndoPreview is null)
        {
            return;
        }

        viewModel.BeginAction(
            "Binding a new undo-only approval to the displayed recovery fingerprint…");
        UndoExecutionWorkflowResult result = await undo.ApproveAndExecuteAsync(
            activeLab,
            latestUndoPreview,
            cancellationToken);
        viewModel.ApplyUndoExecution(result);
    }

    public async Task CreateCorrectionAsync(CancellationToken cancellationToken = default)
    {
        if (!viewModel.CanCreateCorrection ||
            activeLab is null ||
            latestTeaching is null ||
            latestCompilation is null)
        {
            return;
        }

        viewModel.BeginAction(
            "Compiling an explicit clarification into immutable Draft version n+1…");
        SkillCorrectionWorkflowResult result =
            await correction.CreateExplicitClarificationAsync(
                activeLab,
                latestTeaching,
                latestCompilation,
                cancellationToken);
        if (result.IsSuccess)
        {
            latestCompilation = result.CorrectedCompilation;
            latestRehearsal = null;
            latestProduction = null;
            latestUndoPreview = null;
        }

        viewModel.ApplyCorrection(result);
    }

    public async Task ExportCapsuleAsync(CancellationToken cancellationToken = default)
    {
        if (!viewModel.CanExportCapsule)
        {
            return;
        }

        viewModel.BeginAction(
            "Validating an authority-free companion capsule before local CreateNew export…");
        CapsuleExportWorkflowResult result = await capsuleExport.ExportAsync(
            companionSession.CompanionId,
            cancellationToken);
        viewModel.ApplyCapsuleExport(result);
    }
}
