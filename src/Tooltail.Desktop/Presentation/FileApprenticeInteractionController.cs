using System.IO;
using Tooltail.Application.FileSkills;
using Tooltail.Domain.Agents;

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
    private readonly object operationGate = new();
    private Task? initializationTask;
    private CancellationTokenSource? activeOperation;
    private bool pauseRequested;
    private SafeLabGrantResult? activeLab;
    private TeachingWorkflowResult? latestTeaching;
    private SkillCompilationWorkflowResult? latestCompilation;
    private SkillRehearsalWorkflowResult? latestRehearsal;
    private ProductionExecutionWorkflowResult? latestProduction;
    private UndoPlanningWorkflowResult? latestUndoPreview;

    public bool HasActiveOperation
    {
        get
        {
            lock (operationGate)
            {
                return activeOperation is not null;
            }
        }
    }

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

    public bool RequestStop(bool pause)
    {
        CancellationTokenSource operation;
        lock (operationGate)
        {
            if (activeOperation is null)
            {
                return false;
            }

            pauseRequested = pause;
            operation = activeOperation;
            viewModel.ReportControlStopRequested(pause);
        }

        try
        {
            operation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return true;
    }

    public async Task RevokeFolderGrantAsync(
        CancellationToken cancellationToken = default)
    {
        SafeLabGrantResult? current = activeLab;
        if (current is null || !viewModel.CanRevokeFolderGrant)
        {
            return;
        }

        SafeLabGrantResult result = await safeLab.RevokeAsync(
            current,
            cancellationToken);
        if (result.IsSuccess)
        {
            bool operationStillStopping = HasActiveOperation;
            activeLab = null;
            _ = RequestStop(pause: false);
            viewModel.ApplyFolderGrantRevocation(result, operationStillStopping);
            return;
        }

        viewModel.ApplyFolderGrantRevocation(
            result,
            operationStillStopping: HasActiveOperation);
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

        viewModel.BeginAction(
            "Creating a new Tooltail-owned lab and exact folder grant…",
            NormalizedAgentToolKind.File);
        try
        {
            SafeLabGrantResult? result = await RunActiveOperationAsync(
                token => safeLab.CreateAsync(companionSession.CompanionId, token),
                cancellationToken);
            if (result is null)
            {
                return;
            }

            if (result.IsSuccess)
            {
                activeLab = result;
                viewModel.ApplySafeLab(result);
            }
            else
            {
                viewModel.FailAction(
                    result.ReasonCode,
                    $"Safe lab stopped without a grant: {result.ReasonCode}.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            viewModel.FailAction("safe_lab.cancelled", "Safe lab creation was cancelled.");
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or InvalidOperationException)
        {
            viewModel.FailAction(
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

        viewModel.BeginAction(
            "Capturing the authoritative baseline before observation…",
            NormalizedAgentToolKind.File);
        TeachingWorkflowResult? result = await RunActiveOperationAsync(
            token => teaching.StartAsync(activeLab, token),
            cancellationToken);
        if (result is null)
        {
            return;
        }

        viewModel.ApplyTeachingStart(result);
    }

    public async Task StopTeachingAsync(CancellationToken cancellationToken = default)
    {
        if (!viewModel.CanStopTeaching)
        {
            return;
        }

        viewModel.BeginAction(
            "Stopping watcher hints and capturing the authoritative final snapshot…",
            NormalizedAgentToolKind.File);
        TeachingWorkflowResult? result = await RunActiveOperationAsync(
            teaching.StopAsync,
            cancellationToken);
        if (result is null)
        {
            return;
        }

        latestTeaching = result;
        viewModel.ApplyTeachingStop(result);
    }

    public async Task CompileSkillAsync(CancellationToken cancellationToken = default)
    {
        if (!viewModel.CanCompileSkill || activeLab is null || latestTeaching is null)
        {
            return;
        }

        viewModel.BeginAction(
            "Running the deterministic compiler over exact normalized examples…",
            NormalizedAgentToolKind.Other);
        SkillCompilationWorkflowResult? result = await RunActiveOperationAsync(
            token => compiler.CompileAsync(
                activeLab,
                latestTeaching,
                viewModel.CompilerAnswers(),
                token),
            cancellationToken);
        if (result is null)
        {
            return;
        }

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
            "Copying bounded fixtures into a Tooltail-owned root for shared-executor rehearsal…",
            NormalizedAgentToolKind.File);
        SkillRehearsalWorkflowResult? result = await RunActiveOperationAsync(
            token => rehearsal.RehearseAsync(
                activeLab,
                latestCompilation,
                token),
            cancellationToken);
        if (result is null)
        {
            return;
        }

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
            "Binding one production approval to the displayed canonical fingerprint…",
            NormalizedAgentToolKind.File);
        ProductionExecutionWorkflowResult? result =
            await RunActiveOperationAsync(
                token => production.ApproveAndExecuteAsync(
                    activeLab,
                    latestCompilation,
                    latestRehearsal,
                    token),
                cancellationToken);
        if (result is null)
        {
            return;
        }

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
            "Reloading the verified receipt and current snapshot to derive an exact recovery preview…",
            NormalizedAgentToolKind.File);
        UndoPlanningWorkflowResult? result = await RunActiveOperationAsync(
            token => undo.PlanAsync(activeLab, latestProduction, token),
            cancellationToken);
        if (result is null)
        {
            return;
        }

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
            "Binding a new undo-only approval to the displayed recovery fingerprint…",
            NormalizedAgentToolKind.File);
        UndoExecutionWorkflowResult? result = await RunActiveOperationAsync(
            token => undo.ApproveAndExecuteAsync(
                activeLab,
                latestUndoPreview,
                token),
            cancellationToken);
        if (result is null)
        {
            return;
        }

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
            "Compiling an explicit clarification into immutable Draft version n+1…",
            NormalizedAgentToolKind.Other);
        SkillCorrectionWorkflowResult? result =
            await RunActiveOperationAsync(
                token => correction.CreateExplicitClarificationAsync(
                    activeLab,
                    latestTeaching,
                    latestCompilation,
                    token),
                cancellationToken);
        if (result is null)
        {
            return;
        }

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
            "Validating an authority-free companion capsule before local CreateNew export…",
            NormalizedAgentToolKind.File);
        CapsuleExportWorkflowResult? result = await RunActiveOperationAsync(
            token => capsuleExport.ExportAsync(companionSession.CompanionId, token),
            cancellationToken);
        if (result is null)
        {
            return;
        }

        viewModel.ApplyCapsuleExport(result);
    }

    private async Task<T?> RunActiveOperationAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(action);
        using CancellationTokenSource operation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (operationGate)
        {
            if (activeOperation is not null)
            {
                throw new InvalidOperationException(
                    "Only one File Apprentice operation may be active.");
            }

            pauseRequested = false;
            activeOperation = operation;
        }

        try
        {
            return await action(operation.Token);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            bool paused;
            lock (operationGate)
            {
                paused = pauseRequested;
            }

            viewModel.FailAction(
                paused ? "control.paused" : "control.cancelled",
                paused
                    ? "The active operation reached a cooperative boundary and stopped. It will not resume automatically."
                    : "The active operation reached a cooperative boundary and was cancelled.");
            return null;
        }
        finally
        {
            lock (operationGate)
            {
                if (ReferenceEquals(activeOperation, operation))
                {
                    activeOperation = null;
                    pauseRequested = false;
                }
            }
        }
    }
}
