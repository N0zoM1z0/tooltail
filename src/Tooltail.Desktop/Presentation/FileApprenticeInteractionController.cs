using System.IO;
using Tooltail.Application.FileSkills;
using Tooltail.Contracts.Research;
using Tooltail.Domain.Agents;
using Tooltail.Features.FileSkills.Continuity;

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
    private readonly CapsuleImportFileWorkflowService capsuleFileImport;
    private readonly CompanionCapsuleImportService capsuleImport;
    private readonly CapsuleRebindWorkflowService capsuleRebind;
    private readonly ResearchEventRecorder research;
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
    private DateTimeOffset? clarificationPresentedUtc;
    private DateTimeOffset? productionApprovalPresentedUtc;
    private DateTimeOffset? undoApprovalPresentedUtc;
    private CapsuleFilePreviewResult? pendingCapsuleImport;

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
        CapsuleExportWorkflowService capsuleExport,
        CapsuleImportFileWorkflowService capsuleFileImport,
        CompanionCapsuleImportService capsuleImport,
        CapsuleRebindWorkflowService capsuleRebind,
        ResearchEventRecorder research)
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
        ArgumentNullException.ThrowIfNull(capsuleFileImport);
        ArgumentNullException.ThrowIfNull(capsuleImport);
        ArgumentNullException.ThrowIfNull(capsuleRebind);
        ArgumentNullException.ThrowIfNull(research);
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
        this.capsuleFileImport = capsuleFileImport;
        this.capsuleImport = capsuleImport;
        this.capsuleRebind = capsuleRebind;
        this.research = research;
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
            DateTimeOffset requested = research.StartTiming();
            _ = research.RecordAsync(
                pause ? ResearchEventType.PauseRequested : ResearchEventType.CancelRequested,
                requested,
                success: true,
                pause ? "control.pause_requested" : "control.cancel_requested",
                bodyState: ResearchBodyState.PausedOrCancelled);
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

        DateTimeOffset started = research.StartTiming();
        SafeLabGrantResult result = await safeLab.RevokeAsync(
            current,
            cancellationToken);
        if (result.IsSuccess)
        {
            bool operationStillStopping = HasActiveOperation;
            activeLab = null;
            _ = RequestStop(pause: false);
            viewModel.ApplyFolderGrantRevocation(result, operationStillStopping);
            await research.RecordAsync(
                ResearchEventType.FolderGrantRevoked,
                started,
                success: true,
                result.ReasonCode,
                bodyState: ResearchBodyState.PermissionRevoked);
            return;
        }

        viewModel.ApplyFolderGrantRevocation(
            result,
            operationStillStopping: HasActiveOperation);
        await research.RecordAsync(
            ResearchEventType.FolderGrantRevoked,
            started,
            success: false,
            result.ReasonCode,
            bodyState: ResearchBodyState.Failed);
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

        DateTimeOffset started = research.StartTiming();
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

            await research.RecordAsync(
                ResearchEventType.FolderGrantIssued,
                started,
                result.IsSuccess,
                result.ReasonCode,
                count: result.IsSuccess ? 3 : 0,
                bodyState: result.IsSuccess
                    ? ResearchBodyState.ScopedIdle
                    : ResearchBodyState.Failed);
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

        DateTimeOffset started = research.StartTiming();
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
        await research.RecordAsync(
            ResearchEventType.LessonCompleted,
            started,
            result.IsSuccess,
            result.ReasonCode,
            count: result.ExampleCount,
            bodyState: result.IsSuccess
                ? ResearchBodyState.NeedsInput
                : ResearchBodyState.Failed);
    }

    public async Task CompileSkillAsync(CancellationToken cancellationToken = default)
    {
        if (!viewModel.CanCompileSkill || activeLab is null || latestTeaching is null)
        {
            return;
        }

        DateTimeOffset started = research.StartTiming();
        DateTimeOffset? clarificationStarted = clarificationPresentedUtc;
        IReadOnlyList<Tooltail.Contracts.Skills.SkillUserAnswerContract> answers =
            viewModel.CompilerAnswers();
        viewModel.BeginAction(
            "Running the deterministic compiler over exact normalized examples…",
            NormalizedAgentToolKind.Other);
        SkillCompilationWorkflowResult? result = await RunActiveOperationAsync(
            token => compiler.CompileAsync(
                activeLab,
                latestTeaching,
                answers,
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
        if (clarificationStarted is not null)
        {
            await research.RecordAsync(
                ResearchEventType.ClarificationCompleted,
                clarificationStarted.Value,
                result.IsSuccess,
                result.IsSuccess
                    ? "clarification.answers_applied"
                    : "clarification.answers_not_applied",
                count: answers.Count,
                skillVersion: result.Specification?.Version,
                bodyState: result.IsSuccess
                    ? ResearchBodyState.NeedsInput
                    : ResearchBodyState.Failed);
        }

        clarificationPresentedUtc = result.Compilation?.Questions.Count > 0
            ? research.StartTiming()
            : null;
        await research.RecordAsync(
            ResearchEventType.SkillCompiled,
            started,
            result.IsSuccess,
            result.ReasonCode,
            count: result.Compilation?.Questions.Count,
            skillVersion: result.Specification?.Version,
            bodyState: result.IsSuccess || result.Compilation?.Questions.Count > 0
                ? ResearchBodyState.NeedsInput
                : ResearchBodyState.Failed);
    }

    public async Task RehearseSkillAsync(CancellationToken cancellationToken = default)
    {
        if (!viewModel.CanRehearseSkill ||
            activeLab is null ||
            latestCompilation is null)
        {
            return;
        }

        DateTimeOffset started = research.StartTiming();
        productionApprovalPresentedUtc = null;
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
            productionApprovalPresentedUtc = research.StartTiming();
        }

        viewModel.ApplyRehearsal(result);
        await research.RecordAsync(
            ResearchEventType.RehearsalCompleted,
            started,
            result.IsSuccess,
            result.ReasonCode,
            count: result.MatchedFileCount,
            skillVersion: latestCompilation?.Specification?.Version,
            bodyState: result.IsSuccess
                ? ResearchBodyState.NeedsInput
                : ResearchBodyState.Failed);
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

        DateTimeOffset started = research.StartTiming();
        if (productionApprovalPresentedUtc is DateTimeOffset approvalPresented)
        {
            await research.RecordAsync(
                ResearchEventType.ApprovalDecided,
                approvalPresented,
                success: true,
                "approval.production_submitted",
                count: latestRehearsal.ProductionPlan?.Definition.Operations.Count,
                skillVersion: latestCompilation.Specification?.Version,
                bodyState: ResearchBodyState.Working);
            productionApprovalPresentedUtc = null;
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
        await research.RecordAsync(
            ResearchEventType.ExecutionCompleted,
            started,
            result.IsSuccess,
            result.ReasonCode,
            count: result.Execution?.Receipt?.VerifiedStepCount,
            skillVersion: result.SkillVersion?.Number.Value,
            bodyState: result.IsSuccess
                ? ResearchBodyState.CompletedReceipt
                : ResearchBodyState.Failed);
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
            undoApprovalPresentedUtc = research.StartTiming();
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

        DateTimeOffset started = research.StartTiming();
        if (undoApprovalPresentedUtc is DateTimeOffset approvalPresented)
        {
            await research.RecordAsync(
                ResearchEventType.ApprovalDecided,
                approvalPresented,
                success: true,
                "approval.undo_submitted",
                count: latestUndoPreview.OperationCount,
                bodyState: ResearchBodyState.Working);
            undoApprovalPresentedUtc = null;
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
        await research.RecordAsync(
            ResearchEventType.UndoCompleted,
            started,
            result.IsSuccess,
            result.ReasonCode,
            count: result.Execution?.Receipt?.VerifiedSteps.Count,
            bodyState: result.IsSuccess
                ? ResearchBodyState.CompletedReceipt
                : ResearchBodyState.Failed);
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

        DateTimeOffset started = research.StartTiming();
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
            productionApprovalPresentedUtc = null;
            undoApprovalPresentedUtc = null;
        }

        viewModel.ApplyCorrection(result);
        await research.RecordAsync(
            ResearchEventType.CorrectionCompleted,
            started,
            result.IsSuccess,
            result.ReasonCode,
            count: result.CausalProbeChanged ? 1 : 0,
            skillVersion: result.CorrectedCompilation?.Specification?.Version,
            bodyState: result.IsSuccess
                ? ResearchBodyState.NeedsInput
                : ResearchBodyState.Failed);
    }

    public async Task ExportCapsuleAsync(CancellationToken cancellationToken = default)
    {
        if (!viewModel.CanExportCapsule)
        {
            return;
        }

        DateTimeOffset started = research.StartTiming();
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
        await research.RecordAsync(
            ResearchEventType.CapsuleExported,
            started,
            result.IsSuccess,
            result.ReasonCode,
            count: result.SkillVersionCount,
            bodyState: result.IsSuccess
                ? ResearchBodyState.CompletedReceipt
                : ResearchBodyState.Failed);
    }

    public async Task PreviewCapsuleImportAsync(
        string selectedPath,
        CancellationToken cancellationToken = default)
    {
        if (!viewModel.CanPreviewCapsuleImport)
        {
            return;
        }

        viewModel.BeginAction(
            "Reading one bounded local Capsule for an authority-free preview…",
            NormalizedAgentToolKind.File);
        CapsuleFilePreviewResult? result = await RunActiveOperationAsync(
            token => capsuleFileImport.PreviewAsync(selectedPath, token),
            cancellationToken);
        if (result is null)
        {
            return;
        }

        pendingCapsuleImport = result.IsSuccess ? result : null;
        viewModel.ApplyCapsuleImportPreview(result);
    }

    public async Task CommitCapsuleImportAsync(
        CancellationToken cancellationToken = default)
    {
        CapsuleFilePreviewResult? pending = pendingCapsuleImport;
        if (!viewModel.CanCommitCapsuleImport || pending?.ExactBytes is null)
        {
            return;
        }

        viewModel.BeginAction(
            "Atomically importing identity and Stale skills without any grant or approval…",
            NormalizedAgentToolKind.Other);
        CapsuleImportResult? imported = await RunActiveOperationAsync(
            token => capsuleImport.ImportAsync(
                pending.ExactBytes,
                companionSession.CompanionId,
                token),
            cancellationToken);
        pendingCapsuleImport = null;
        if (imported is null)
        {
            return;
        }

        FileApprenticeStartupResult? restarted = null;
        if (imported.IsSuccess && imported.ImportedCompanionId is not null)
        {
            companionSession.Restore(imported.ImportedCompanionId.Value);
            try
            {
                restarted = await startupService.InitializeAsync(CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is IOException or
                UnauthorizedAccessException or InvalidOperationException)
            {
                restarted = FileApprenticeStartupResult.Failure(
                    "capsule.import_committed_reload_failed");
            }

            if (restarted.IsReady)
            {
                companionSession.Restore(restarted.Workspace!.Companion.Id);
                latestTeaching = null;
                latestCompilation = null;
                latestRehearsal = null;
                latestProduction = null;
                latestUndoPreview = null;
                activeLab = null;
                viewModel.Apply(restarted);
            }
        }

        viewModel.ApplyCapsuleImport(
            imported,
            restarted,
            pending.ByteCount,
            pending.Sha256!);
    }

    public async Task RebindNextImportedSkillAsync(
        CancellationToken cancellationToken = default)
    {
        if (!viewModel.CanRebindImportedSkill || activeLab is null)
        {
            return;
        }

        viewModel.BeginAction(
            "Creating a new Draft that changes only the imported skill's grant binding…",
            NormalizedAgentToolKind.Other);
        CapsuleRebindWorkflowResult? result = await RunActiveOperationAsync(
            token => capsuleRebind.RebindNextAsync(
                companionSession.CompanionId,
                activeLab,
                token),
            cancellationToken);
        if (result is null)
        {
            return;
        }

        if (result.IsSuccess)
        {
            latestCompilation = result.Compilation;
            latestRehearsal = null;
            latestProduction = null;
            latestUndoPreview = null;
        }

        viewModel.ApplyCapsuleRebind(result);
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
