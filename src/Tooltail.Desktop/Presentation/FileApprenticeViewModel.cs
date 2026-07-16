using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Tooltail.Application.Abstractions;
using Tooltail.Application.FileSkills;
using Tooltail.Contracts.Skills;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Permissions;
using Tooltail.Features.FileSkills.Compilation;
using Tooltail.Features.FileSkills.Presentation;

namespace Tooltail.Desktop.Presentation;

public sealed class FileApprenticeViewModel : INotifyPropertyChanged
{
    private readonly IClock clock;
    private string headline = "Loading persisted apprentice state…";
    private string reasonCode = "startup.pending";
    private string companionName = "Not loaded";
    private string grantState = "No folder grant loaded.";
    private string skillState = "No skill loaded.";
    private string lessonState = "No teaching episode loaded.";
    private string executionState = "No execution loaded.";
    private string recoveryState = "Recovery scan has not run.";
    private string labPath = "No safe lab created.";
    private string lastActionMessage = "Waiting for local SQLite initialization.";
    private bool isReady;
    private bool isFirstRun;
    private bool isBusy;
    private bool hasActiveGrant;
    private bool isObserving;
    private bool hasCompilableTeaching;
    private SkillCardViewModel? skillCard;
    private RehearsalPlanViewModel? rehearsalPlan;
    private ExecutionReceiptViewModel? executionReceipt;
    private bool productionAttempted;

    public FileApprenticeViewModel(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.clock = clock;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Headline
    {
        get => headline;
        private set => SetProperty(ref headline, value);
    }

    public string ReasonCode
    {
        get => reasonCode;
        private set => SetProperty(ref reasonCode, value);
    }

    public string CompanionName
    {
        get => companionName;
        private set => SetProperty(ref companionName, value);
    }

    public string GrantState
    {
        get => grantState;
        private set => SetProperty(ref grantState, value);
    }

    public string SkillState
    {
        get => skillState;
        private set => SetProperty(ref skillState, value);
    }

    public string LessonState
    {
        get => lessonState;
        private set => SetProperty(ref lessonState, value);
    }

    public string ExecutionState
    {
        get => executionState;
        private set => SetProperty(ref executionState, value);
    }

    public string RecoveryState
    {
        get => recoveryState;
        private set => SetProperty(ref recoveryState, value);
    }

    public string LabPath
    {
        get => labPath;
        private set => SetProperty(ref labPath, value);
    }

    public string LastActionMessage
    {
        get => lastActionMessage;
        private set => SetProperty(ref lastActionMessage, value);
    }

    public bool IsReady
    {
        get => isReady;
        private set
        {
            if (SetProperty(ref isReady, value))
            {
                OnPropertyChanged(nameof(CanCreateSafeLab));
                OnPropertyChanged(nameof(CanStartTeaching));
                OnPropertyChanged(nameof(CanStopTeaching));
                OnPropertyChanged(nameof(CanCompileSkill));
                OnPropertyChanged(nameof(CanRehearseSkill));
                OnPropertyChanged(nameof(CanApproveAndExecute));
            }
        }
    }

    public bool IsFirstRun
    {
        get => isFirstRun;
        private set => SetProperty(ref isFirstRun, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                OnPropertyChanged(nameof(CanCreateSafeLab));
                OnPropertyChanged(nameof(CanStartTeaching));
                OnPropertyChanged(nameof(CanStopTeaching));
                OnPropertyChanged(nameof(CanCompileSkill));
                OnPropertyChanged(nameof(CanRehearseSkill));
                OnPropertyChanged(nameof(CanApproveAndExecute));
            }
        }
    }

    public bool CanCreateSafeLab => IsReady && !IsBusy && !hasActiveGrant;

    public bool CanStartTeaching => IsReady && !IsBusy && hasActiveGrant && !isObserving;

    public bool CanStopTeaching => IsReady && !IsBusy && isObserving;

    public bool CanCompileSkill =>
        IsReady && !IsBusy && hasCompilableTeaching && !isObserving && SkillCard is null;

    public bool CanRehearseSkill =>
        IsReady && !IsBusy && SkillCard is not null && RehearsalPlan is null;

    public bool CanApproveAndExecute =>
        IsReady && !IsBusy && RehearsalPlan is not null && !productionAttempted;

    public ObservableCollection<CompilerQuestionChoiceViewModel> CompilerQuestions { get; } = [];

    public SkillCardViewModel? SkillCard
    {
        get => skillCard;
        private set
        {
            if (SetProperty(ref skillCard, value))
            {
                OnPropertyChanged(nameof(HasSkillCard));
                OnPropertyChanged(nameof(CanCompileSkill));
                OnPropertyChanged(nameof(CanRehearseSkill));
                OnPropertyChanged(nameof(CanApproveAndExecute));
            }
        }
    }

    public bool HasSkillCard => SkillCard is not null;

    public RehearsalPlanViewModel? RehearsalPlan
    {
        get => rehearsalPlan;
        private set
        {
            if (SetProperty(ref rehearsalPlan, value))
            {
                OnPropertyChanged(nameof(HasRehearsalPlan));
                OnPropertyChanged(nameof(CanRehearseSkill));
                OnPropertyChanged(nameof(CanApproveAndExecute));
            }
        }
    }

    public bool HasRehearsalPlan => RehearsalPlan is not null;

    public ExecutionReceiptViewModel? ExecutionReceipt
    {
        get => executionReceipt;
        private set
        {
            if (SetProperty(ref executionReceipt, value))
            {
                OnPropertyChanged(nameof(HasExecutionReceipt));
            }
        }
    }

    public bool HasExecutionReceipt => ExecutionReceipt is not null;

    public void Apply(FileApprenticeStartupResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ReasonCode = result.ReasonCode;
        IsReady = result.IsReady;
        IsFirstRun = result.CreatedCompanion;
        IsBusy = false;
        if (!result.IsReady)
        {
            Headline = "Local apprentice state needs inspection";
            LastActionMessage = $"Startup stopped safely: {result.ReasonCode}.";
            return;
        }

        var workspace = result.Workspace!;
        CompanionName = workspace.Companion.DisplayName;
        Headline = result.CreatedCompanion
            ? "First run ready — choose a safe lab before teaching"
            : "Persisted apprentice state restored";
        GrantState = workspace.Grants.Count == 0
            ? "No folder grant. Window context grants no file authority."
            : string.Join(
                "; ",
                workspace.Grants.Take(3).Select(grant =>
                    $"{grant.Grant.Id.Value:D}: {EffectiveGrantState(grant.Grant)}; " +
                    $"{grant.Grant.Capabilities.Count.ToString(CultureInfo.InvariantCulture)} exact actions"));
        hasActiveGrant = workspace.Grants.Any(grant =>
            grant.Grant.State == ResourceGrantState.Active &&
            (grant.Grant.ExpiresAt is null || grant.Grant.ExpiresAt > clock.UtcNow));
        OnPropertyChanged(nameof(CanCreateSafeLab));
        OnPropertyChanged(nameof(CanStartTeaching));
        OnPropertyChanged(nameof(CanStopTeaching));
        OnPropertyChanged(nameof(CanCompileSkill));
        OnPropertyChanged(nameof(CanRehearseSkill));
        OnPropertyChanged(nameof(CanApproveAndExecute));
        SkillState = workspace.CurrentSkills.Count == 0
            ? "No learned skill."
            : string.Join(
                "; ",
                workspace.CurrentSkills.Take(3).Select(skill =>
                    $"{skill.DisplayName} v{skill.Version.Number.Value.ToString(CultureInfo.InvariantCulture)} " +
                    $"({skill.Version.Lifecycle})"));
        LessonState = workspace.TeachingEpisodes.Count == 0
            ? "No teaching episode."
            : FormatLesson(workspace.TeachingEpisodes[0]);
        ExecutionState = workspace.Executions.Count == 0
            ? "No execution receipt or journal."
            : FormatExecution(workspace.Executions[0]);
        RecoveryState = result.Recovery!.Candidates.Count == 0
            ? "No interrupted execution requires recovery."
            : $"{result.Recovery.Candidates.Count.ToString(CultureInfo.InvariantCulture)} " +
                "interrupted execution(s) require inspect-first recovery; nothing was replayed.";
        LastActionMessage = result.CreatedCompanion
            ? "Created one local companion identity. No login, model key, telemetry, or grant was created."
            : "Reloaded bounded local state from SQLite and completed a non-mutating recovery scan.";
    }

    public void BeginAction(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        IsBusy = true;
        LastActionMessage = message;
    }

    public void CompleteAction(string reasonCode, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ReasonCode = reasonCode;
        IsBusy = false;
        LastActionMessage = message;
    }

    public void ApplySafeLab(SafeLabGrantResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.IsSuccess || result.Grant is null || result.CanonicalLabPath is null)
        {
            throw new ArgumentException("Only a successful safe lab result can be applied.", nameof(result));
        }

        hasActiveGrant = true;
        LabPath = result.CanonicalLabPath;
        GrantState = $"Safe lab grant {result.Grant.Id.Value:D}: active; " +
            $"{result.Grant.Capabilities.Count.ToString(CultureInfo.InvariantCulture)} exact actions; " +
            $"expires {result.Grant.ExpiresAt:O}.";
        Headline = "Safe lab granted — ready to capture a teaching baseline";
        CompleteAction(
            result.ReasonCode,
            "Created three synthetic invoice PDFs in a new Tooltail-owned folder. " +
            "No existing file was overwritten or removed.");
        OnPropertyChanged(nameof(CanCreateSafeLab));
        OnPropertyChanged(nameof(CanStartTeaching));
    }

    public void ApplyRestoredSafeLab(SafeLabGrantResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.IsSuccess || result.Grant is null || result.CanonicalLabPath is null)
        {
            throw new ArgumentException("Only a restored safe lab can be applied.", nameof(result));
        }

        hasActiveGrant = true;
        LabPath = result.CanonicalLabPath;
        OnPropertyChanged(nameof(CanCreateSafeLab));
        OnPropertyChanged(nameof(CanStartTeaching));
    }

    public void ApplyTeachingStart(TeachingWorkflowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        isObserving = result.IsSuccess;
        LessonState = result.Episode is null
            ? "Teaching did not start."
            : $"{result.Episode.State}; evidence {result.Episode.EvidenceState}.";
        Headline = result.IsSuccess
            ? "Observing the safe lab — perform the demonstrated file moves now"
            : "Teaching baseline stopped safely";
        CompleteAction(
            result.ReasonCode,
            result.IsSuccess
                ? "Baseline is committed and watcher hints are active. Move at least two PDFs in File Explorer, then choose Stop and reconcile."
                : $"Teaching did not become active: {result.ReasonCode}.");
        OnPropertyChanged(nameof(CanStartTeaching));
        OnPropertyChanged(nameof(CanStopTeaching));
        OnPropertyChanged(nameof(CanCompileSkill));
    }

    public void ApplyTeachingStop(TeachingWorkflowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        isObserving = false;
        hasCompilableTeaching = result.IsSuccess;
        LessonState = result.Episode is null
            ? "No reconciled teaching episode."
            : $"{result.Episode.State}; evidence {result.Episode.EvidenceState}; " +
                $"{result.ExampleCount.ToString(CultureInfo.InvariantCulture)} example(s).";
        Headline = result.IsSuccess
            ? "Teaching evidence reconciled — ready to compile"
            : "Teaching evidence needs correction or another demonstration";
        CompleteAction(
            result.ReasonCode,
            result.Reconciliation is null
                ? $"Teaching stopped: {result.ReasonCode}."
                : $"Final snapshot is authoritative: {result.Reconciliation.Status}; " +
                    $"{result.Reconciliation.Effects.Count.ToString(CultureInfo.InvariantCulture)} normalized effect(s).");
        OnPropertyChanged(nameof(CanStartTeaching));
        OnPropertyChanged(nameof(CanStopTeaching));
        OnPropertyChanged(nameof(CanCompileSkill));
    }

    public IReadOnlyList<SkillUserAnswerContract> CompilerAnswers() =>
        CompilerQuestions
            .Where(static question => !string.IsNullOrWhiteSpace(question.SelectedValue))
            .Select(static question => new SkillUserAnswerContract
            {
                QuestionCode = question.Code,
                SelectedValue = question.SelectedValue!,
            })
            .ToArray();

    public void ApplyCompilation(SkillCompilationWorkflowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        CompilerQuestions.Clear();
        if (result.Compilation?.Status == SkillCompilationStatus.NeedsClarification)
        {
            foreach (CompilerQuestion question in result.Compilation.Questions)
            {
                CompilerQuestions.Add(new CompilerQuestionChoiceViewModel(question));
            }

            Headline = "Clarification required before a SkillSpec can exist";
            CompleteAction(
                result.ReasonCode,
                $"The deterministic compiler localized ambiguity to {CompilerQuestions.Count.ToString(CultureInfo.InvariantCulture)} typed question(s). No skill was saved.");
            OnPropertyChanged(nameof(CanCompileSkill));
            return;
        }

        if (result.IsSuccess && result.Card is not null)
        {
            SkillCard = result.Card;
            RehearsalPlan = null;
            SkillState = $"{result.Specification!.Name} v{result.Specification.Version.ToString(CultureInfo.InvariantCulture)} (Draft)";
            Headline = "Draft SkillSpec saved — inspect and rehearse before approval";
            CompleteAction(
                result.ReasonCode,
                "One deterministic candidate was persisted. The compiler did not approve or execute it.");
            return;
        }

        Headline = "No safe SkillSpec candidate";
        CompleteAction(result.ReasonCode, $"Compilation stopped: {result.ReasonCode}.");
        OnPropertyChanged(nameof(CanCompileSkill));
    }

    public void ApplyRehearsal(SkillRehearsalWorkflowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsSuccess &&
            result.Rehearsal?.IsPassed == true &&
            result.ProductionPlan is not null)
        {
            RehearsalPlan = RehearsalPlanViewModel.From(
                result.ProductionPlan,
                result.Rehearsal.Execution!.Receipt!.VerifiedStepCount,
                result.MatchedFileCount,
                result.Rehearsal.Cleanup!.ReasonCode,
                result.Rehearsal.Retirement!.ReasonCode);
            ExecutionState =
                $"Rehearsal verified {result.Rehearsal.Execution.Receipt.VerifiedStepCount.ToString(CultureInfo.InvariantCulture)} " +
                "step(s); production plan is not approved.";
            Headline = "Rehearsal passed — inspect the exact production plan before approval";
            CompleteAction(
                result.ReasonCode,
                "The shared executor verified the temporary copy, removed the owned workspace, retired its grant, and persisted an unapproved production plan.");
            return;
        }

        Headline = result.Rehearsal?.Cleanup is { IsSuccess: false }
            ? "Rehearsal stopped with an owned cleanup residual"
            : "Rehearsal did not pass";
        CompleteAction(
            result.ReasonCode,
            $"No production approval is available: {result.ReasonCode}.");
        OnPropertyChanged(nameof(CanRehearseSkill));
    }

    public void ApplyProductionExecution(ProductionExecutionWorkflowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        productionAttempted = true;
        if (result.Execution?.Receipt is not null)
        {
            ExecutionReceipt = ExecutionReceiptViewModel.From(
                result.Execution.Receipt);
        }

        if (result.Card is not null)
        {
            SkillCard = result.Card;
        }

        if (result.IsSuccess &&
            result.Execution?.IsVerified == true &&
            result.SkillVersion is not null)
        {
            SkillState =
                $"{SkillCard!.OriginalName} v{result.SkillVersion.Number.Value.ToString(CultureInfo.InvariantCulture)} " +
                $"({result.SkillVersion.Lifecycle})";
            ExecutionState =
                $"Production {result.Execution.Status}; " +
                $"{result.Execution.Receipt!.VerifiedStepCount.ToString(CultureInfo.InvariantCulture)} verified step(s); receipt present.";
            Headline = "Production execution verified — receipt and Undo window are inspectable";
            CompleteAction(
                result.ReasonCode,
                "The exact displayed approval was consumed once, every mutation was journaled and verified, and a durable receipt was stored.");
        }
        else
        {
            Headline = result.Execution?.Receipt is not null
                ? "Execution verified but persisted lifecycle needs inspection"
                : "Production execution stopped safely";
            CompleteAction(
                result.ReasonCode,
                $"Production did not reach a fully persisted success state: {result.ReasonCode}.");
        }

        OnPropertyChanged(nameof(CanApproveAndExecute));
    }

    private string EffectiveGrantState(LocalFolderGrant grant)
    {
        if (grant.State == ResourceGrantState.Revoked)
        {
            return $"revoked ({grant.RevocationReason})";
        }

        return grant.ExpiresAt is not null && grant.ExpiresAt <= clock.UtcNow
            ? "expired"
            : "active";
    }

    private static string FormatLesson(
        Tooltail.Application.Abstractions.TeachingEpisodeSummaryStateRecord lesson) =>
        $"{lesson.Status}; evidence {lesson.EvidenceStatus}; " +
        $"{lesson.ExampleCount.ToString(CultureInfo.InvariantCulture)} example(s).";

    private static string FormatExecution(
        Tooltail.Application.Abstractions.ExecutionSummaryStateRecord execution) =>
        $"{execution.Kind} {execution.Status}; skill v" +
        $"{execution.SkillVersion.Value.ToString(CultureInfo.InvariantCulture)}; " +
        $"receipt {(execution.HasReceipt ? "present" : "absent")}.";

    private bool SetProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class CompilerQuestionChoiceViewModel
{
    public CompilerQuestionChoiceViewModel(CompilerQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        Code = question.Code;
        Prompt = question.Prompt;
        Options = question.Options;
    }

    public string Code { get; }

    public string Prompt { get; }

    public IReadOnlyList<CompilerQuestionOption> Options { get; }

    public string? SelectedValue { get; set; }
}

public sealed record RehearsalPlanViewModel(
    string PlanId,
    string Fingerprint,
    string GrantId,
    string ExpiresUtc,
    int MatchedFileCount,
    int VerifiedRehearsalStepCount,
    string CleanupReasonCode,
    string RetirementReasonCode,
    IReadOnlyList<RehearsalOperationViewModel> Operations)
{
    public static RehearsalPlanViewModel From(
        ExecutionPlan plan,
        int verifiedRehearsalStepCount,
        int matchedFileCount,
        string cleanupReasonCode,
        string retirementReasonCode)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new RehearsalPlanViewModel(
            plan.Definition.Id.Value.ToString("D"),
            plan.Fingerprint.Value,
            plan.Definition.GrantId.Value.ToString("D"),
            plan.Definition.ExpiresUtc.ToString("O", CultureInfo.InvariantCulture),
            matchedFileCount,
            verifiedRehearsalStepCount,
            cleanupReasonCode,
            retirementReasonCode,
            plan.Definition.Operations.Select(RehearsalOperationViewModel.From).ToArray());
    }
}

public sealed record RehearsalOperationViewModel(
    int Sequence,
    string Primitive,
    string Source,
    string Destination,
    string ExpectedResult)
{
    public static RehearsalOperationViewModel From(PlannedFileOperation operation) =>
        new(
            operation.Sequence,
            operation.Primitive switch
            {
                FilePrimitive.EnsureDirectory => "ensure_directory",
                FilePrimitive.RenameFile => "rename_file",
                FilePrimitive.MoveFile => "move_file",
                FilePrimitive.CopyFile => "copy_file",
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            },
            operation.SourceRelativePath ?? "(none)",
            operation.DestinationRelativePath,
            operation.ExpectedDestinationState.ToString());
}

public sealed record ExecutionReceiptViewModel(
    string ReceiptId,
    string ExecutionId,
    string PlanId,
    string PlanFingerprint,
    string CompletedUtc,
    string UndoAvailableUntilUtc,
    int VerifiedStepCount,
    IReadOnlyList<ReceiptStepViewModel> VerifiedSteps)
{
    public static ExecutionReceiptViewModel From(ExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return new ExecutionReceiptViewModel(
            receipt.Id.Value.ToString("D"),
            receipt.ExecutionId.Value.ToString("D"),
            receipt.PlanId.Value.ToString("D"),
            receipt.PlanFingerprint.Value,
            receipt.CompletedUtc.ToString("O", CultureInfo.InvariantCulture),
            receipt.UndoAvailableUntilUtc?.ToString("O", CultureInfo.InvariantCulture) ??
                "Unavailable",
            receipt.VerifiedStepCount,
            receipt.VerifiedSteps.Select(ReceiptStepViewModel.From).ToArray());
    }
}

public sealed record ReceiptStepViewModel(
    int Sequence,
    string Primitive,
    string Source,
    string Destination,
    string DestinationKind,
    string DestinationIdentity,
    string? ContentSha256)
{
    public static ReceiptStepViewModel From(VerifiedStepEvidence evidence) =>
        new(
            evidence.StepSequence,
            evidence.Primitive.ToString(),
            evidence.SourceRelativePath ?? "(none)",
            evidence.DestinationRelativePath,
            evidence.Destination.Kind.ToString(),
            evidence.Destination.EntryIdentity,
            evidence.Destination.ContentHash?.Value);
}
