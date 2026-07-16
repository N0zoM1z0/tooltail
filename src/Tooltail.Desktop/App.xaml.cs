using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tooltail.Application;
using Tooltail.Application.Abstractions;
using Tooltail.Application.FileSkills;
using Tooltail.Application.Windows;
using Tooltail.Desktop.Controls;
using Tooltail.Desktop.Presentation;
using Tooltail.Domain.Agents;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Features.FileSkills.Continuity;
using Tooltail.Features.FileSkills.Observation;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Features.FileSkills.Snapshots;
using Tooltail.Infrastructure.LocalResearch;
using Tooltail.Infrastructure.Sqlite;
using Tooltail.Platform.Windows.FileSystem;
using Tooltail.Platform.Windows.Windowing;

namespace Tooltail.Desktop;

public partial class App : System.Windows.Application
{
    private IHost? host;
    private HomeWindow? homeWindow;
    private InspectorWindow? inspectorWindow;
    private MainWindow? agentBodyWindow;
    private WindowSurfaceCoordinator? surfaceCoordinator;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                ApplicationName = typeof(App).Assembly.GetName().Name,
                Args = [],
            });

        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Services.AddTooltailApplication();
        string databasePath = ResolveDatabasePath(e.Args);
        builder.Services.AddSingleton(
            new SqliteDatabaseOptions(
                databasePath,
                typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0"));
        builder.Services.AddSingleton(
            new LocalResearchOptions(
                Path.GetDirectoryName(Path.GetDirectoryName(databasePath))!));
        builder.Services.AddSingleton<TooltailSqliteDatabase>();
        builder.Services.AddSingleton<LocalStateDeletionService>();
        builder.Services.AddSingleton<SqliteFileSkillStateStore>();
        builder.Services.AddSingleton<IFileSkillStateStore>(static services =>
            services.GetRequiredService<SqliteFileSkillStateStore>());
        builder.Services.AddSingleton<SqliteExecutionJournalStore>();
        builder.Services.AddSingleton<IExecutionJournalStore>(static services =>
            services.GetRequiredService<SqliteExecutionJournalStore>());
        builder.Services.AddSingleton<IExecutionJournalReader>(static services =>
            services.GetRequiredService<SqliteExecutionJournalStore>());
        builder.Services.AddSingleton<WindowsFileSystemPathProbe>();
        builder.Services.AddSingleton<IFileSystemPathProbe>(static services =>
            services.GetRequiredService<WindowsFileSystemPathProbe>());
        builder.Services.AddSingleton<WindowsPathSafetyService>();
        builder.Services.AddSingleton<FolderSnapshotService>();
        builder.Services.AddSingleton<IWatcherHintSourceFactory, FileSystemWatcherHintSourceFactory>();
        builder.Services.AddSingleton<TeachingObservationService>();
        builder.Services.AddSingleton<WindowsWindowSystem>();
        builder.Services.AddSingleton<IWindowSystem>(static services =>
            services.GetRequiredService<WindowsWindowSystem>());
        builder.Services.AddSingleton<IPhysicalPointerSource>(static services =>
            services.GetRequiredService<WindowsWindowSystem>());
        builder.Services.AddSingleton<ICoordinateSpace, WindowsCoordinateSpace>();
        builder.Services.AddSingleton(WindowBindingPolicy.Default);
        builder.Services.AddSingleton<WindowBindingService>();
        builder.Services.AddSingleton<DesktopCompanionSession>();
        builder.Services.AddSingleton<WindowLeaseViewModel>();
        builder.Services.AddSingleton<WindowLeaseInteractionController>();
        builder.Services.AddSingleton<FileApprenticeViewModel>();
        builder.Services.AddSingleton<SafeLabGrantService>();
        builder.Services.AddSingleton<TeachingWorkflowService>();
        builder.Services.AddSingleton<SkillCompilationWorkflowService>();
        builder.Services.AddSingleton<SkillRehearsalWorkflowService>();
        builder.Services.AddSingleton<ProductionExecutionWorkflowService>();
        builder.Services.AddSingleton<UndoWorkflowService>();
        builder.Services.AddSingleton<SkillCorrectionWorkflowService>();
        builder.Services.AddSingleton<CapsuleExportWorkflowService>();
        builder.Services.AddSingleton<LocalResearchStore>();
        builder.Services.AddSingleton<ResearchStudyViewModel>();
        builder.Services.AddSingleton<LocalDataLifecycleViewModel>();
        builder.Services.AddSingleton<ResearchEventRecorder>();
        builder.Services.AddSingleton<FileApprenticeInteractionController>();
        builder.Services.AddSingleton<ResearchInteractionController>();
        builder.Services.AddSingleton<LocalDataLifecycleController>();
        builder.Services.AddSingleton<WindowSurfaceCoordinator>();
        builder.Services.AddSingleton<HomeWindow>();
        builder.Services.AddSingleton<InspectorWindow>();
        builder.Services.AddSingleton<PetWindow>();
        builder.Services.AddSingleton<TetherWindow>();
        builder.Services.AddTransient<MainWindow>();

        host = builder.Build();

        bool legacySmoke = e.Args.Contains("--smoke-test", StringComparer.Ordinal) ||
            e.Args.Contains("--skill-card-smoke-test", StringComparer.Ordinal) ||
            e.Args.Contains("--agent-body-smoke-test", StringComparer.Ordinal);
        if (!legacySmoke)
        {
            LocalStateDeletionResult deletionRecovery = host.Services
                .GetRequiredService<LocalStateDeletionService>()
                .RecoverPendingDeletion();
            if (!deletionRecovery.IsSuccess)
            {
                MessageBox.Show(
                    "Tooltail found an invalid or incomplete local-state deletion request. " +
                    "It did not open or replace the product database. Inspect or restore the " +
                    "Tooltail-owned state directory before trying again.",
                    "Tooltail local state recovery required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Environment.ExitCode = 1;
                Shutdown(1);
                return;
            }

            _ = host.Services.GetRequiredService<TooltailSqliteDatabase>()
                .InitializeAsync()
                .GetAwaiter()
                .GetResult();
            _ = host.Services.GetRequiredService<LocalResearchStore>()
                .InitializeAsync()
                .GetAwaiter()
                .GetResult();
        }

        host.Start();
        if (!legacySmoke)
        {
            StartWindowShell(e.Args);
            return;
        }

        MainWindow mainWindow = host.Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        if (e.Args.Contains("--skill-card-smoke-test", StringComparer.Ordinal))
        {
            mainWindow.Content = new SkillCardControl();
        }

        if (e.Args.Contains("--agent-body-smoke-test", StringComparer.Ordinal))
        {
            mainWindow.ConfigureAgentBodySmokeTest();
        }

        mainWindow.ContentRendered += CloseAfterSmokeRender;

        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            host?.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        finally
        {
            host?.Dispose();
            base.OnExit(e);
        }
    }

    private void CloseAfterSmokeRender(object? sender, EventArgs e)
    {
        if (MainWindow is null)
        {
            return;
        }

        MainWindow.ContentRendered -= CloseAfterSmokeRender;
        MainWindow.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            () => MainWindow.Close());
    }

    private void StartWindowShell(IReadOnlyCollection<string> args)
    {
        homeWindow = host!.Services.GetRequiredService<HomeWindow>();
        inspectorWindow = host.Services.GetRequiredService<InspectorWindow>();
        PetWindow petWindow = host.Services.GetRequiredService<PetWindow>();
        TetherWindow tetherWindow = host.Services.GetRequiredService<TetherWindow>();
        surfaceCoordinator = host.Services.GetRequiredService<WindowSurfaceCoordinator>();

        MainWindow = homeWindow;
        homeWindow.InspectorRequested += OnInspectorRequested;
        homeWindow.AgentBodyRequested += OnAgentBodyRequested;
        homeWindow.Closing += OnHomeClosing;
        inspectorWindow.HomeRequested += OnHomeRequested;
        inspectorWindow.AgentBodyRequested += OnAgentBodyRequested;
        petWindow.InspectorRequested += OnInspectorRequested;
        petWindow.HomeRequested += OnHomeRequested;

        homeWindow.Show();
        inspectorWindow.Owner = homeWindow;
        surfaceCoordinator.Start(petWindow, tetherWindow);
        if (args.Contains("--window-shell-smoke-test", StringComparer.Ordinal))
        {
            homeWindow.ContentRendered += CloseAfterWindowShellSmoke;
        }
    }

    private void OnInspectorRequested(object? sender, EventArgs eventArgs)
    {
        if (inspectorWindow is null)
        {
            return;
        }

        if (!inspectorWindow.IsVisible)
        {
            inspectorWindow.Show();
        }

        inspectorWindow.WindowState = WindowState.Normal;
        inspectorWindow.Activate();
        inspectorWindow.Focus();
        ResearchEventRecorder research = host!.Services
            .GetRequiredService<ResearchEventRecorder>();
        DateTimeOffset started = research.StartTiming();
        _ = research.RecordAsync(
            Tooltail.Contracts.Research.ResearchEventType.InspectorOpened,
            started,
            success: true,
            "research.inspector_opened");
    }

    private void OnHomeRequested(object? sender, EventArgs eventArgs)
    {
        if (homeWindow is null)
        {
            return;
        }

        if (!homeWindow.IsVisible)
        {
            homeWindow.Show();
        }

        homeWindow.WindowState = WindowState.Normal;
        homeWindow.Activate();
        homeWindow.Focus();
    }

    private void OnAgentBodyRequested(object? sender, EventArgs eventArgs)
    {
        if (agentBodyWindow is null || !agentBodyWindow.IsLoaded)
        {
            agentBodyWindow = host!.Services.GetRequiredService<MainWindow>();
            agentBodyWindow.Closed += OnAgentBodyClosed;
            agentBodyWindow.Show();
        }

        agentBodyWindow.WindowState = WindowState.Normal;
        agentBodyWindow.Activate();
        agentBodyWindow.Focus();
    }

    private void OnAgentBodyClosed(object? sender, EventArgs eventArgs)
    {
        if (agentBodyWindow is not null)
        {
            agentBodyWindow.Closed -= OnAgentBodyClosed;
            agentBodyWindow = null;
        }
    }

    private void OnHomeClosing(object? sender, System.ComponentModel.CancelEventArgs eventArgs)
    {
        inspectorWindow?.PrepareForShutdown();
        surfaceCoordinator?.Dispose();
    }

    private void CloseAfterWindowShellSmoke(object? sender, EventArgs eventArgs)
    {
        if (homeWindow is null)
        {
            return;
        }

        homeWindow.ContentRendered -= CloseAfterWindowShellSmoke;
        homeWindow.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            () =>
            {
                _ = VerifyAndCloseWindowShellSmokeAsync();
            });
    }

    private async Task VerifyAndCloseWindowShellSmokeAsync()
    {
        try
        {
            FileApprenticeInteractionController apprentice = host!.Services
                .GetRequiredService<FileApprenticeInteractionController>();
            await apprentice.InitializeAsync();
            FileApprenticeViewModel apprenticeViewModel =
                host.Services.GetRequiredService<FileApprenticeViewModel>();
            if (!apprenticeViewModel.IsReady)
            {
                throw new InvalidOperationException(
                    "The persisted File Apprentice startup state was not ready.");
            }

            ResearchInteractionController research = host.Services
                .GetRequiredService<ResearchInteractionController>();
            ResearchStudyViewModel researchViewModel = host.Services
                .GetRequiredService<ResearchStudyViewModel>();
            string researchRoot = Path.Combine(
                Path.GetDirectoryName(Path.GetDirectoryName(
                    host.Services.GetRequiredService<TooltailSqliteDatabase>()
                        .DatabasePath))!,
                "Research");
            await research.InitializeAsync();
            if (researchViewModel.IsEnabled || Directory.Exists(researchRoot))
            {
                throw new InvalidOperationException(
                    "Research storage was not absent and visibly off on first launch.");
            }

            await research.EnableAsync();
            if (!researchViewModel.IsEnabled ||
                researchViewModel.EventCount != 2 ||
                !Directory.Exists(researchRoot))
            {
                throw new InvalidOperationException(
                    "Explicit local research opt-in did not create the bounded consented session.");
            }

            WindowLeaseViewModel bodyViewModel = host.Services
                .GetRequiredService<WindowLeaseViewModel>();
            AssertBody(
                bodyViewModel,
                CompanionBodyState.HomeIdle,
                expectedTool: null,
                "clean persisted startup");

            await apprentice.CreateSafeLabAsync();
            AssertLastAcceptedTool(
                apprenticeViewModel,
                NormalizedAgentToolKind.File,
                "safe-lab activity");
            string[] expectedLabFiles =
            [
                "invoice-alpha.pdf",
                "invoice-beta.pdf",
                "invoice-edge.pdf",
            ];
            if (!string.Equals(
                    apprenticeViewModel.ReasonCode,
                    "safe_lab.grant_issued",
                    StringComparison.Ordinal) ||
                expectedLabFiles.Any(file =>
                    !File.Exists(Path.Combine(apprenticeViewModel.LabPath, file))))
            {
                throw new InvalidOperationException(
                    "The safe-lab grant smoke did not produce its exact owned fixture.");
            }
            AssertBody(
                bodyViewModel,
                CompanionBodyState.ScopedIdle,
                expectedTool: null,
                "persisted file scope");

            await apprentice.StartTeachingAsync();
            AssertLastAcceptedTool(
                apprenticeViewModel,
                NormalizedAgentToolKind.File,
                "baseline capture");
            if (!apprenticeViewModel.CanStopTeaching)
            {
                throw new InvalidOperationException(
                    "The teaching baseline did not enter active observation.");
            }
            AssertBody(
                bodyViewModel,
                CompanionBodyState.Observing,
                expectedTool: null,
                "committed observation");

            string invoices = Path.Combine(apprenticeViewModel.LabPath, "Invoices");
            Directory.CreateDirectory(invoices);
            File.Move(
                Path.Combine(apprenticeViewModel.LabPath, "invoice-alpha.pdf"),
                Path.Combine(invoices, "filed-invoice-alpha.pdf"));
            File.Move(
                Path.Combine(apprenticeViewModel.LabPath, "invoice-beta.pdf"),
                Path.Combine(invoices, "filed-invoice-beta.pdf"));
            await apprentice.StopTeachingAsync();
            AssertLastAcceptedTool(
                apprenticeViewModel,
                NormalizedAgentToolKind.File,
                "authoritative reconciliation");
            if (!string.Equals(
                    apprenticeViewModel.ReasonCode,
                    "reconcile.complete",
                    StringComparison.Ordinal) ||
                !apprenticeViewModel.LessonState.Contains(
                    "2 example(s)",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The authoritative teaching snapshots did not reconcile two examples.");
            }
            AssertBody(
                bodyViewModel,
                CompanionBodyState.NeedsInput,
                expectedTool: null,
                "reconciled lesson awaiting compile");

            await apprentice.CompileSkillAsync();
            AssertLastAcceptedTool(
                apprenticeViewModel,
                NormalizedAgentToolKind.Other,
                "deterministic compilation");
            if (apprenticeViewModel.CompilerQuestions.Count is < 1 or > 2)
            {
                throw new InvalidOperationException(
                    "The deterministic compiler did not localize ambiguity to typed questions.");
            }

            IFileSkillStateStore stateStore =
                host.Services.GetRequiredService<IFileSkillStateStore>();
            CompanionId companionId =
                host.Services.GetRequiredService<DesktopCompanionSession>().CompanionId;
            StateReadResult<FileSkillWorkspaceStateRecord> ambiguousWorkspace =
                await stateStore.LoadWorkspaceStateAsync(companionId);
            if (!ambiguousWorkspace.IsSuccess ||
                ambiguousWorkspace.Value!.CurrentSkills.Count != 0)
            {
                throw new InvalidOperationException(
                    "The ambiguous compiler result persisted a skill before clarification.");
            }
            AssertBody(
                bodyViewModel,
                CompanionBodyState.NeedsInput,
                expectedTool: null,
                "typed compiler questions");

            foreach (CompilerQuestionChoiceViewModel question in
                     apprenticeViewModel.CompilerQuestions)
            {
                question.SelectedValue = question.Code switch
                {
                    "match.origin_scope" => "same_directory",
                    "match.filename_scope" => "contains_token",
                    "transform.filename" => question.Options[0].Value,
                    _ => throw new InvalidOperationException(
                        "The compiler emitted an unknown smoke clarification."),
                };
            }

            await apprentice.CompileSkillAsync();
            if (!string.Equals(
                    apprenticeViewModel.ReasonCode,
                    "compiler.draft_persisted",
                    StringComparison.Ordinal) ||
                apprenticeViewModel.SkillCard is null ||
                apprenticeViewModel.SkillCard.Lifecycle != "Draft")
            {
                throw new InvalidOperationException(
                    "The clarified Draft SkillSpec and Skill Card were not persisted.");
            }
            AssertBody(
                bodyViewModel,
                CompanionBodyState.NeedsInput,
                expectedTool: null,
                "persisted Draft awaiting rehearsal");

            StateReadResult<FileSkillWorkspaceStateRecord> persistedWorkspace =
                await stateStore.LoadWorkspaceStateAsync(companionId);
            if (!persistedWorkspace.IsSuccess ||
                persistedWorkspace.Value!.CurrentSkills.Count != 1 ||
                persistedWorkspace.Value.CurrentSkills[0].Version.Lifecycle !=
                    Tooltail.Domain.Skills.SkillLifecycleState.Draft)
            {
                throw new InvalidOperationException(
                    "The clarified Draft SkillSpec did not pass repository readback.");
            }

            await apprentice.RehearseSkillAsync();
            if (!string.Equals(
                    apprenticeViewModel.ReasonCode,
                    "rehearsal.production_plan_ready",
                    StringComparison.Ordinal) ||
                apprenticeViewModel.RehearsalPlan is null ||
                apprenticeViewModel.RehearsalPlan.Operations.Count != 1 ||
                !File.Exists(Path.Combine(apprenticeViewModel.LabPath, "invoice-edge.pdf")))
            {
                throw new InvalidOperationException(
                    "The verified rehearsal or unapproved production plan was not rendered truthfully.");
            }
            AssertBody(
                bodyViewModel,
                CompanionBodyState.NeedsInput,
                expectedTool: null,
                "unapproved production plan");

            StateReadResult<FileSkillWorkspaceStateRecord> rehearsedWorkspace =
                await stateStore.LoadWorkspaceStateAsync(companionId);
            if (!rehearsedWorkspace.IsSuccess ||
                rehearsedWorkspace.Value!.Executions.Count != 1 ||
                !rehearsedWorkspace.Value.Executions[0].HasReceipt ||
                rehearsedWorkspace.Value.Grants.Count(grant =>
                    grant.Grant.State ==
                        Tooltail.Domain.Permissions.ResourceGrantState.Active) != 1 ||
                rehearsedWorkspace.Value.Grants.Count(grant =>
                    grant.Grant.State ==
                        Tooltail.Domain.Permissions.ResourceGrantState.Revoked) != 1)
            {
                throw new InvalidOperationException(
                    "The rehearsal receipt or temporary-grant retirement did not pass repository readback.");
            }

            StateReadResult<StoredPlanDocument> productionPlan =
                await stateStore.LoadPlanDocumentAsync(
                    new PlanId(Guid.Parse(apprenticeViewModel.RehearsalPlan.PlanId)));
            if (!productionPlan.IsSuccess ||
                productionPlan.Value!.Fingerprint.Value !=
                    apprenticeViewModel.RehearsalPlan.Fingerprint)
            {
                throw new InvalidOperationException(
                    "The exact unapproved production plan did not pass canonical repository readback.");
            }

            string rehearsalRoot = Path.Combine(
                Path.GetDirectoryName(
                    Path.GetDirectoryName(
                        host.Services.GetRequiredService<TooltailSqliteDatabase>()
                            .DatabasePath)!)!,
                "Rehearsals");
            if (!Directory.Exists(rehearsalRoot) ||
                Directory.EnumerateFileSystemEntries(rehearsalRoot).Any())
            {
                throw new InvalidOperationException(
                    "The Tooltail-owned rehearsal workspace was not removed after verification.");
            }

            string productionFingerprint =
                apprenticeViewModel.RehearsalPlan.Fingerprint;
            await apprentice.ApproveAndExecuteAsync();
            AssertLastAcceptedTool(
                apprenticeViewModel,
                NormalizedAgentToolKind.File,
                "approved production activity");
            string expectedProductionTarget = Path.Combine(
                apprenticeViewModel.LabPath,
                "Invoices",
                "filed-invoice-edge.pdf");
            if (!string.Equals(
                    apprenticeViewModel.ReasonCode,
                    "execution.production_verified",
                    StringComparison.Ordinal) ||
                apprenticeViewModel.ExecutionReceipt is null ||
                apprenticeViewModel.ExecutionReceipt.PlanFingerprint !=
                    productionFingerprint ||
                apprenticeViewModel.SkillCard?.Lifecycle != "Practiced" ||
                File.Exists(Path.Combine(
                    apprenticeViewModel.LabPath,
                    "invoice-edge.pdf")) ||
                !File.Exists(expectedProductionTarget))
            {
                throw new InvalidOperationException(
                    "The exact production approval, verified effect, or receipt was not rendered truthfully.");
            }
            AssertBody(
                bodyViewModel,
                CompanionBodyState.CompletedReceipt,
                expectedTool: null,
                "verified production receipt");

            StateReadResult<FileSkillWorkspaceStateRecord> executedWorkspace =
                await stateStore.LoadWorkspaceStateAsync(companionId);
            if (!executedWorkspace.IsSuccess ||
                executedWorkspace.Value!.CurrentSkills[0].Version.Lifecycle !=
                    Tooltail.Domain.Skills.SkillLifecycleState.Practiced ||
                executedWorkspace.Value.Executions.Count != 2 ||
                executedWorkspace.Value.Executions.Any(execution => !execution.HasReceipt))
            {
                throw new InvalidOperationException(
                    "The Practiced lifecycle or durable production receipt did not pass repository readback.");
            }

            ExecutionReceiptReadResult receiptReadback =
                await host.Services.GetRequiredService<IExecutionJournalReader>()
                    .LoadReceiptAsync(
                        new ExecutionId(Guid.Parse(
                            apprenticeViewModel.ExecutionReceipt.ExecutionId)));
            if (!receiptReadback.IsSuccess ||
                receiptReadback.StandardReceipt!.PlanFingerprint.Value !=
                    productionFingerprint ||
                receiptReadback.StandardReceipt.VerifiedStepCount != 1)
            {
                throw new InvalidOperationException(
                    "The production receipt did not survive strict journal-backed readback.");
            }

            await apprentice.PlanUndoAsync();
            if (!string.Equals(
                    apprenticeViewModel.ReasonCode,
                    "undo.preview_ready",
                    StringComparison.Ordinal) ||
                apprenticeViewModel.UndoPlan is null ||
                apprenticeViewModel.UndoPlan.Operations.Count != 1 ||
                apprenticeViewModel.UndoPlan.Operations[0].Primitive != "MoveBack" ||
                File.Exists(Path.Combine(
                    apprenticeViewModel.LabPath,
                    "invoice-edge.pdf")) ||
                !File.Exists(expectedProductionTarget))
            {
                throw new InvalidOperationException(
                    "The exact recovery preview mutated state or did not render the inverse proof.");
            }
            AssertBody(
                bodyViewModel,
                CompanionBodyState.NeedsInput,
                expectedTool: null,
                "unapproved recovery plan");

            StateReadResult<StoredPlanDocument> recoveryPlanReadback =
                await stateStore.LoadPlanDocumentAsync(
                    new PlanId(Guid.Parse(apprenticeViewModel.UndoPlan.PlanId)));
            if (!recoveryPlanReadback.IsSuccess ||
                recoveryPlanReadback.Value!.Kind != PersistedPlanKind.Recovery ||
                recoveryPlanReadback.Value.Fingerprint.Value !=
                    apprenticeViewModel.UndoPlan.Fingerprint)
            {
                throw new InvalidOperationException(
                    "The canonical recovery plan did not pass repository readback.");
            }

            string recoveryFingerprint = apprenticeViewModel.UndoPlan.Fingerprint;
            await apprentice.ApproveAndExecuteUndoAsync();
            if (!string.Equals(
                    apprenticeViewModel.ReasonCode,
                    "undo.production_restored",
                    StringComparison.Ordinal) ||
                apprenticeViewModel.UndoReceipt is null ||
                apprenticeViewModel.UndoReceipt.PlanFingerprint != recoveryFingerprint ||
                !File.Exists(Path.Combine(
                    apprenticeViewModel.LabPath,
                    "invoice-edge.pdf")) ||
                File.Exists(expectedProductionTarget))
            {
                throw new InvalidOperationException(
                    "The separately approved Undo did not verify exact restoration.");
            }
            AssertBody(
                bodyViewModel,
                CompanionBodyState.CompletedReceipt,
                expectedTool: null,
                "verified Undo receipt");

            StateReadResult<FileSkillWorkspaceStateRecord> restoredWorkspace =
                await stateStore.LoadWorkspaceStateAsync(companionId);
            if (!restoredWorkspace.IsSuccess ||
                restoredWorkspace.Value!.Executions.Count != 3 ||
                restoredWorkspace.Value.Executions.Any(execution => !execution.HasReceipt))
            {
                throw new InvalidOperationException(
                    "The recovery execution or receipt did not pass workspace readback.");
            }

            ExecutionReceiptReadResult recoveryReceipt =
                await host.Services.GetRequiredService<IExecutionJournalReader>()
                    .LoadReceiptAsync(
                        new ExecutionId(Guid.Parse(
                            apprenticeViewModel.UndoReceipt.ExecutionId)));
            ExecutionJournalReadResult rolledBackOriginal =
                await host.Services.GetRequiredService<IExecutionJournalReader>()
                    .LoadJournalAsync(
                        new ExecutionId(Guid.Parse(
                            apprenticeViewModel.ExecutionReceipt.ExecutionId)));
            if (!recoveryReceipt.IsSuccess ||
                recoveryReceipt.Kind != PersistedReceiptKind.Recovery ||
                recoveryReceipt.RecoveryReceipt!.PlanFingerprint.Value !=
                    recoveryFingerprint ||
                rolledBackOriginal.Journal!.AssessStep(1).Status !=
                    StepRecoveryStatus.RolledBack)
            {
                throw new InvalidOperationException(
                    "The recovery receipt or original rollback link failed strict readback.");
            }

            await apprentice.CreateCorrectionAsync();
            if (!string.Equals(
                    apprenticeViewModel.ReasonCode,
                    "correction.draft_persisted",
                    StringComparison.Ordinal) ||
                apprenticeViewModel.SkillCard?.Version != 2 ||
                apprenticeViewModel.SkillCard.Lifecycle != "Draft" ||
                apprenticeViewModel.SkillCard.SemanticDiff.Count == 0 ||
                !apprenticeViewModel.CanRehearseSkill)
            {
                throw new InvalidOperationException(
                    "The parent-linked corrected Draft or causal semantic diff was not rendered truthfully.");
            }
            AssertBody(
                bodyViewModel,
                CompanionBodyState.NeedsInput,
                expectedTool: null,
                "corrected Draft awaiting new rehearsal");

            StateReadResult<FileSkillWorkspaceStateRecord> correctedWorkspace =
                await stateStore.LoadWorkspaceStateAsync(companionId);
            StateReadResult<IReadOnlyList<SkillVersionStateRecord>> versionHistory =
                await stateStore.LoadSkillVersionsAsync(
                    new SkillId(Guid.Parse(apprenticeViewModel.SkillCard.SkillId)));
            if (!correctedWorkspace.IsSuccess ||
                correctedWorkspace.Value!.CurrentSkills[0].Version.Number.Value != 2 ||
                correctedWorkspace.Value.CurrentSkills[0].Version.Lifecycle !=
                    Tooltail.Domain.Skills.SkillLifecycleState.Draft ||
                correctedWorkspace.Value.Executions.Count != 3 ||
                !versionHistory.IsSuccess ||
                versionHistory.Value!.Count != 2 ||
                versionHistory.Value[1].Version.Parent?.Value != 1 ||
                versionHistory.Value[1].ApprovedUtc is not null)
            {
                throw new InvalidOperationException(
                    "The immutable v1/v2 correction lineage or retained receipts failed repository readback.");
            }

            await apprentice.ExportCapsuleAsync();
            if (!string.Equals(
                    apprenticeViewModel.ReasonCode,
                    "capsule.exported",
                    StringComparison.Ordinal) ||
                apprenticeViewModel.CapsuleExport is not
                {
                    SkillVersionCount: 2,
                    CreatesAuthority: false,
                    CanImport: false,
                    SkillsRequireRebind: true,
                } ||
                !File.Exists(apprenticeViewModel.CapsuleExport.CanonicalPath))
            {
                throw new InvalidOperationException(
                    "The validated authority-free capsule was not exported or rendered truthfully.");
            }
            AssertBody(
                bodyViewModel,
                CompanionBodyState.NeedsInput,
                expectedTool: null,
                "corrected Draft still outranks capsule output");

            byte[] capsuleBytes = await File.ReadAllBytesAsync(
                apprenticeViewModel.CapsuleExport.CanonicalPath);
            CapsuleImportPreview capsulePreview =
                CompanionCapsuleService.ParseForImport(capsuleBytes);
            if (!capsulePreview.IsSuccess ||
                capsulePreview.CreatesAuthority ||
                capsulePreview.CanImport ||
                !capsulePreview.SkillsRequireRebind ||
                capsulePreview.Capsule!.Skills.Count != 2 ||
                capsulePreview.Capsule.Skills[1].SkillSpec.Provenance.ParentVersion != 1 ||
                System.Text.Encoding.UTF8.GetString(capsuleBytes).Contains(
                    apprenticeViewModel.LabPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The exported capsule readback contained authority, lost lineage, or leaked the physical lab path.");
            }

            WindowLeaseInteractionController criticalControls = host.Services
                .GetRequiredService<WindowLeaseInteractionController>();
            Task pausedRehearsal = apprentice.RehearseSkillAsync();
            criticalControls.RequestPause();
            if (!string.Equals(
                    apprenticeViewModel.BodyEvidenceReasonCode,
                    "control.pause_requested",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The keyboard safe-pause control did not interruptively reach the active File Apprentice operation.");
            }

            await pausedRehearsal;
            if (apprenticeViewModel.CurrentBody.State !=
                    CompanionBodyState.PausedOrCancelled ||
                apprenticeViewModel.RehearsalPlan is not null ||
                !apprenticeViewModel.CanRehearseSkill)
            {
                throw new InvalidOperationException(
                    "Cancelled corrected-skill rehearsal claimed a plan, remained active, or could not be safely retried.");
            }

            await apprentice.RehearseSkillAsync();
            if (!string.Equals(
                    apprenticeViewModel.ReasonCode,
                    "rehearsal.production_plan_ready",
                    StringComparison.Ordinal) ||
                apprenticeViewModel.RehearsalPlan is null ||
                apprenticeViewModel.RehearsalPlan.Fingerprint == productionFingerprint)
            {
                throw new InvalidOperationException(
                    "Corrected Draft v2 did not produce a fresh causally distinct production plan after safe retry.");
            }

            string correctedProductionFingerprint =
                apprenticeViewModel.RehearsalPlan.Fingerprint;
            await apprentice.ApproveAndExecuteAsync();
            if (!string.Equals(
                    apprenticeViewModel.ReasonCode,
                    "execution.production_verified",
                    StringComparison.Ordinal) ||
                apprenticeViewModel.SkillCard is not
                {
                    Version: 2,
                    Lifecycle: "Practiced",
                } ||
                apprenticeViewModel.ExecutionReceipt?.PlanFingerprint !=
                    correctedProductionFingerprint ||
                File.Exists(Path.Combine(
                    apprenticeViewModel.LabPath,
                    "invoice-edge.pdf")) ||
                !File.Exists(expectedProductionTarget))
            {
                throw new InvalidOperationException(
                    "Corrected v2 did not execute its fresh approved plan and return a verified receipt.");
            }

            FileApprenticeStartupService startup = host.Services
                .GetRequiredService<FileApprenticeStartupService>();
            FileApprenticeStartupResult restarted = await startup.InitializeAsync();
            FileApprenticeViewModel restartedViewModel = new(
                host.Services.GetRequiredService<IClock>());
            restartedViewModel.Apply(restarted);
            if (!restarted.IsReady || restarted.CreatedCompanion ||
                restarted.Recovery!.Candidates.Count != 0 ||
                restartedViewModel.CurrentBody.State !=
                    CompanionBodyState.CompletedReceipt ||
                !restartedViewModel.SkillState.Contains(
                    "v2 (Practiced)",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Restart reconstruction did not restore corrected v2, its verified receipt, and a clean recovery scan.");
            }

            GrantId activeFolderGrantId = correctedWorkspace.Value.Grants
                .Single(grant => grant.Grant.State ==
                    Tooltail.Domain.Permissions.ResourceGrantState.Active)
                .Grant.Id;
            await criticalControls.RevokeFolderGrantAsync();
            if (!string.Equals(
                    apprenticeViewModel.ReasonCode,
                    "safe_lab.grant_revoked",
                    StringComparison.Ordinal) ||
                apprenticeViewModel.CurrentBody.State !=
                    CompanionBodyState.PermissionRevoked ||
                apprenticeViewModel.CanRevokeFolderGrant ||
                apprenticeViewModel.CanRehearseSkill ||
                apprenticeViewModel.CanApproveAndExecute ||
                apprenticeViewModel.CanPlanUndo ||
                apprenticeViewModel.CanApproveUndo ||
                !File.Exists(expectedProductionTarget))
            {
                throw new InvalidOperationException(
                    "Durable folder-grant revocation did not stop future authority or preserve the current lab tree.");
            }

            StateReadResult<FileSkillWorkspaceStateRecord> revokedWorkspace =
                await stateStore.LoadWorkspaceStateAsync(companionId);
            if (!revokedWorkspace.IsSuccess ||
                revokedWorkspace.Value!.Grants.Count(grant =>
                    grant.Grant.Id == activeFolderGrantId &&
                    grant.Grant.State ==
                        Tooltail.Domain.Permissions.ResourceGrantState.Revoked) != 1)
            {
                throw new InvalidOperationException(
                    "The exact folder ResourceGrant revocation did not survive repository readback.");
            }

            FileApprenticeStartupResult revokedRestart = await startup.InitializeAsync();
            FileApprenticeViewModel revokedRestartViewModel = new(
                host.Services.GetRequiredService<IClock>());
            revokedRestartViewModel.Apply(revokedRestart);
            if (!revokedRestart.IsReady ||
                revokedRestartViewModel.CurrentBody.State !=
                    CompanionBodyState.PermissionRevoked ||
                revokedRestartViewModel.CanPlanUndo ||
                revokedRestartViewModel.CanRehearseSkill)
            {
                throw new InvalidOperationException(
                    "Restart did not preserve revoked authority as interruptively visible and non-executable.");
            }

            surfaceCoordinator!.VerifyAmbientStyles();
            OnInspectorRequested(this, EventArgs.Empty);
            inspectorWindow!.UpdateLayout();

            researchViewModel.SelectedRating = 6;
            await research.SubmitRatingAsync();
            await research.PreviewAsync();
            if (!researchViewModel.HasPreview ||
                researchViewModel.EventCount < 10 ||
                researchViewModel.PreviewJsonl.Contains(
                    "invoice-edge.pdf",
                    StringComparison.OrdinalIgnoreCase) ||
                researchViewModel.PreviewJsonl.Contains(
                    apprenticeViewModel.LabPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Research preview was missing closed workflow summaries or retained raw lab data.");
            }

            await research.ExportAsync();
            string researchExportPath = researchViewModel.ExportPath;
            if (!File.Exists(researchExportPath) ||
                new FileInfo(researchExportPath).Length == 0)
            {
                throw new InvalidOperationException(
                    "Reviewed research JSONL was not exported with a non-empty local artifact.");
            }

            await research.DeleteAllAsync();
            await research.PreviewAsync();
            if (researchViewModel.IsEnabled ||
                researchViewModel.EventCount != 0 ||
                researchViewModel.HasPreview ||
                new FileInfo(researchExportPath).Length != 0)
            {
                throw new InvalidOperationException(
                    "One-click research deletion did not disable consent and truncate exact reviewed artifacts.");
            }

            string databasePath = host.Services
                .GetRequiredService<TooltailSqliteDatabase>()
                .DatabasePath;
            string preservedLabPath = expectedProductionTarget;
            string preservedCapsulePath =
                apprenticeViewModel.CapsuleExport.CanonicalPath;
            LocalDataLifecycleViewModel localData = host.Services
                .GetRequiredService<LocalDataLifecycleViewModel>();
            LocalDataLifecycleController localDataController = host.Services
                .GetRequiredService<LocalDataLifecycleController>();
            localDataController.PrepareDeletion();
            if (!localData.HasPreview ||
                localData.CanDelete ||
                localData.DeletedCategories.Count != 6 ||
                localData.PreservedCategories.Count != 5)
            {
                throw new InvalidOperationException(
                    "The local-state deletion preview did not expose its exact two-sided boundary.");
            }

            localData.ConfirmationText = "delete local state";
            if (localData.CanDelete)
            {
                throw new InvalidOperationException(
                    "A case-insensitive local-state confirmation was accepted.");
            }

            localData.ConfirmationText = LocalDataLifecycleViewModel.RequiredConfirmation;
            if (!localData.CanDelete)
            {
                throw new InvalidOperationException(
                    "The exact local-state confirmation did not enable the final action.");
            }

            LocalStateDeletionResult deleted = await localDataController.DeleteAsync();
            string deletionIntent = Path.Combine(
                Path.GetDirectoryName(databasePath)!,
                "local-state-deletion.intent.json");
            if (!deleted.IsSuccess ||
                !deleted.RequiresShutdown ||
                deleted.RequiresRecovery ||
                File.Exists(databasePath) ||
                File.Exists($"{databasePath}-wal") ||
                File.Exists($"{databasePath}-shm") ||
                File.Exists(deletionIntent) ||
                !File.Exists(preservedLabPath) ||
                !File.Exists(preservedCapsulePath))
            {
                throw new InvalidOperationException(
                    "The exact local product-state deletion or preserved lab/Capsule boundary failed.");
            }
        }
        catch (InvalidOperationException)
        {
            Environment.ExitCode = 1;
        }
        finally
        {
            inspectorWindow?.PrepareForShutdown();
            homeWindow?.Close();
        }
    }

    private static void AssertBody(
        WindowLeaseViewModel viewModel,
        CompanionBodyState expectedState,
        NormalizedAgentToolKind? expectedTool,
        string checkpoint)
    {
        CompanionBodyProjection body = viewModel.PetBody;
        if (body.State != expectedState || body.ToolKind != expectedTool)
        {
            throw new InvalidOperationException(
                $"The deterministic body was not truthful at {checkpoint}: " +
                $"expected {expectedState}/{expectedTool}, got {body.State}/{body.ToolKind}.");
        }
    }

    private static void AssertLastAcceptedTool(
        FileApprenticeViewModel viewModel,
        NormalizedAgentToolKind expectedTool,
        string checkpoint)
    {
        if (viewModel.LastAcceptedToolKind != expectedTool)
        {
            throw new InvalidOperationException(
                $"The body tool prop was not typed truthfully at {checkpoint}: " +
                $"expected {expectedTool}, got {viewModel.LastAcceptedToolKind}.");
        }
    }

    private static string ResolveDatabasePath(IReadOnlyCollection<string> args)
    {
        bool smoke = args.Any(static argument =>
            argument.EndsWith("-smoke-test", StringComparison.Ordinal));
        string root = smoke
            ? Path.Combine(
                Path.GetTempPath(),
                "Tooltail",
                "Smoke",
                Environment.ProcessId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture))
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Tooltail");
        return Path.Combine(root, "state", "tooltail.db");
    }
}
