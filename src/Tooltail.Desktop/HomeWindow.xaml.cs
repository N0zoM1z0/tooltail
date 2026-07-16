using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Tooltail.Desktop.Controls;
using Tooltail.Desktop.Presentation;
using Tooltail.Features.FileSkills.Presentation;

namespace Tooltail.Desktop;

public partial class HomeWindow : Window
{
    private readonly WindowLeaseInteractionController interactions;
    private readonly FileApprenticeInteractionController apprenticeInteractions;
    private readonly ResearchInteractionController researchInteractions;
    private readonly LocalDataLifecycleController localDataLifecycle;
    private readonly DiagnosticExportController diagnosticExport;
    private bool loaded;

    public HomeWindow(
        WindowLeaseViewModel viewModel,
        WindowLeaseInteractionController interactions,
        FileApprenticeViewModel fileApprentice,
        FileApprenticeInteractionController apprenticeInteractions,
        ResearchStudyViewModel research,
        ResearchInteractionController researchInteractions,
        LocalDataLifecycleViewModel localData,
        LocalDataLifecycleController localDataLifecycle,
        DiagnosticExportViewModel diagnostic,
        DiagnosticExportController diagnosticExport)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(interactions);
        ArgumentNullException.ThrowIfNull(fileApprentice);
        ArgumentNullException.ThrowIfNull(apprenticeInteractions);
        ArgumentNullException.ThrowIfNull(research);
        ArgumentNullException.ThrowIfNull(researchInteractions);
        ArgumentNullException.ThrowIfNull(localData);
        ArgumentNullException.ThrowIfNull(localDataLifecycle);
        ArgumentNullException.ThrowIfNull(diagnostic);
        ArgumentNullException.ThrowIfNull(diagnosticExport);
        InitializeComponent();
        DataContext = viewModel;
        this.interactions = interactions;
        FileApprentice = fileApprentice;
        this.apprenticeInteractions = apprenticeInteractions;
        Research = research;
        this.researchInteractions = researchInteractions;
        LocalData = localData;
        this.localDataLifecycle = localDataLifecycle;
        Diagnostic = diagnostic;
        this.diagnosticExport = diagnosticExport;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public event EventHandler? InspectorRequested;

    public event EventHandler? AgentBodyRequested;

    public FileApprenticeViewModel FileApprentice { get; }

    public ResearchStudyViewModel Research { get; }

    public LocalDataLifecycleViewModel LocalData { get; }

    public DiagnosticExportViewModel Diagnostic { get; }

    private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (loaded)
        {
            return;
        }

        loaded = true;
        await Task.WhenAll(
            interactions.RefreshTargetsAsync(),
            apprenticeInteractions.InitializeAsync(),
            researchInteractions.InitializeAsync());
    }

    private async void OnRefreshTargetsClick(object sender, RoutedEventArgs eventArgs) =>
        await interactions.RefreshTargetsAsync();

    private async void OnCreateSafeLabClick(object sender, RoutedEventArgs eventArgs) =>
        await apprenticeInteractions.CreateSafeLabAsync();

    private async void OnSelectExistingFolderClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (!FileApprentice.CanSelectExistingFolder)
        {
            return;
        }

        OpenFolderDialog dialog = new()
        {
            Multiselect = false,
            Title = "Select one local fixed-drive folder for an exact Tooltail grant",
        };
        if (dialog.ShowDialog(this) == true)
        {
            await apprenticeInteractions.PreviewExistingFolderAsync(dialog.FolderName);
        }
    }

    private async void OnConfirmExistingFolderClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        await apprenticeInteractions.ConfirmExistingFolderGrantAsync();

    private async void OnStartTeachingClick(object sender, RoutedEventArgs eventArgs) =>
        await apprenticeInteractions.StartTeachingAsync();

    private async void OnStopTeachingClick(object sender, RoutedEventArgs eventArgs) =>
        await apprenticeInteractions.StopTeachingAsync();

    private async void OnCompileSkillClick(object sender, RoutedEventArgs eventArgs) =>
        await apprenticeInteractions.CompileSkillAsync();

    private async void OnRehearseSkillClick(object sender, RoutedEventArgs eventArgs) =>
        await apprenticeInteractions.RehearseSkillAsync();

    private async void OnApproveAndExecuteClick(object sender, RoutedEventArgs eventArgs) =>
        await apprenticeInteractions.ApproveAndExecuteAsync();

    private async void OnPlanUndoClick(object sender, RoutedEventArgs eventArgs) =>
        await apprenticeInteractions.PlanUndoAsync();

    private async void OnApproveUndoClick(object sender, RoutedEventArgs eventArgs) =>
        await apprenticeInteractions.ApproveAndExecuteUndoAsync();

    private async void OnCreateCorrectionClick(object sender, RoutedEventArgs eventArgs) =>
        await apprenticeInteractions.CreateCorrectionAsync();

    private async void OnExportCapsuleClick(object sender, RoutedEventArgs eventArgs) =>
        await apprenticeInteractions.ExportCapsuleAsync();

    private async void OnPreviewCapsuleImportClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (!FileApprentice.CanPreviewCapsuleImport)
        {
            return;
        }

        OpenFileDialog dialog = new()
        {
            AddExtension = true,
            CheckFileExists = true,
            CheckPathExists = true,
            DereferenceLinks = false,
            Filter = "Tooltail Capsule (*.tooltail-capsule.json)|*.tooltail-capsule.json",
            Multiselect = false,
            Title = "Preview an authority-free Tooltail Capsule",
            ValidateNames = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            await apprenticeInteractions.PreviewCapsuleImportAsync(dialog.FileName);
        }
    }

    private async void OnCommitCapsuleImportClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        await apprenticeInteractions.CommitCapsuleImportAsync();

    private async void OnRebindImportedSkillClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        await apprenticeInteractions.RebindNextImportedSkillAsync();

    private async void OnEnableResearchClick(object sender, RoutedEventArgs eventArgs) =>
        await researchInteractions.EnableAsync();

    private async void OnPreviewResearchClick(object sender, RoutedEventArgs eventArgs) =>
        await researchInteractions.PreviewAsync();

    private async void OnExportResearchClick(object sender, RoutedEventArgs eventArgs) =>
        await researchInteractions.ExportAsync();

    private async void OnDeleteResearchClick(object sender, RoutedEventArgs eventArgs) =>
        await researchInteractions.DeleteAllAsync();

    private void OnPrepareLocalDataDeletionClick(object sender, RoutedEventArgs eventArgs) =>
        localDataLifecycle.PrepareDeletion();

    private async void OnDeleteLocalDataClick(object sender, RoutedEventArgs eventArgs)
    {
        Tooltail.Infrastructure.Sqlite.LocalStateDeletionResult result =
            await localDataLifecycle.DeleteAsync();
        if (result.RequiresShutdown)
        {
            System.Windows.Application.Current.Shutdown(result.IsSuccess ? 0 : 1);
        }
    }

    private async void OnPreviewDiagnosticClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        await diagnosticExport.PreviewAsync();

    private async void OnExportDiagnosticClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        await diagnosticExport.ExportAsync();

    private async void OnResetStudyFixtureClick(object sender, RoutedEventArgs eventArgs) =>
        await researchInteractions.ResetStudyFixtureAsync();

    private async void OnSubmitRatingClick(object sender, RoutedEventArgs eventArgs) =>
        await researchInteractions.SubmitRatingAsync();

    private async void OnSkillCardActionRequested(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (eventArgs is not SkillCardActionRequestedEventArgs requested)
        {
            return;
        }

        if (requested.Action == SkillCardActionCode.Rehearse)
        {
            await apprenticeInteractions.RehearseSkillAsync();
            return;
        }

        if (requested.Action == SkillCardActionCode.Approve)
        {
            await apprenticeInteractions.ApproveAndExecuteAsync();
            return;
        }

        if (requested.Action == SkillCardActionCode.Correct)
        {
            await apprenticeInteractions.CreateCorrectionAsync();
            return;
        }

        if (requested.Action == SkillCardActionCode.Export)
        {
            await apprenticeInteractions.ExportCapsuleAsync();
            return;
        }

        FileApprentice.CompleteAction(
            "skill_card.action_not_connected",
            $"{requested.Action} is not connected in this verified M5 checkpoint.");
    }

    private void OnCompilerAnswerChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (sender is ComboBox
            {
                DataContext: CompilerQuestionChoiceViewModel question,
                SelectedValue: string selected,
            })
        {
            question.SelectedValue = selected;
        }
    }

    private async void OnAttachSelectedClick(object sender, RoutedEventArgs eventArgs) =>
        await interactions.AttachSelectedAsync();

    private void OnOpenInspectorClick(object sender, RoutedEventArgs eventArgs) =>
        InspectorRequested?.Invoke(this, EventArgs.Empty);

    private async void OnUnbindClick(object sender, RoutedEventArgs eventArgs) =>
        await interactions.RevokeAsync();

    private async void OnRevokeFolderGrantClick(object sender, RoutedEventArgs eventArgs) =>
        await interactions.RevokeFolderGrantAsync();

    private async void OnReturnHomeClick(object sender, RoutedEventArgs eventArgs) =>
        await interactions.ReturnHomeAsync();

    private void OnPauseClick(object sender, RoutedEventArgs eventArgs) =>
        interactions.RequestPause();

    private void OnCancelClick(object sender, RoutedEventArgs eventArgs) =>
        interactions.RequestCancel();

    private void OnOpenAgentBodyClick(object sender, RoutedEventArgs eventArgs) =>
        AgentBodyRequested?.Invoke(this, EventArgs.Empty);

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        Loaded -= OnLoaded;
        Closed -= OnClosed;
    }
}
