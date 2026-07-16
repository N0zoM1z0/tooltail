using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Tooltail.Application.Abstractions;
using Tooltail.Application.FileSkills;
using Tooltail.Contracts.Skills;
using Tooltail.Domain.Agents;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
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
    private UndoPlanViewModel? undoPlan;
    private UndoReceiptViewModel? undoReceipt;
    private CapsuleExportViewModel? capsuleExport;
    private bool productionAttempted;
    private bool undoAttempted;
    private bool correctionAttempted;
    private CompanionActivityFacts bodyFacts = new();
    private CompanionBodyProjection currentBody = CompanionActivityProjector.Project(new());
    private string bodyEvidenceReasonCode = "startup.pending";
    private string bodyEvidenceSummary =
        "Persisted apprentice truth has not been loaded yet.";
    private bool pendingInputBeforeAction;
    private NormalizedAgentToolKind? lastAcceptedToolKind;
    private bool folderGrantRevoked;

    public FileApprenticeViewModel(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.clock = clock;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Headline
    {
        get => headline;
        private set
        {
            if (SetProperty(ref headline, value))
            {
                OnPropertyChanged(nameof(BodyAccessibleName));
            }
        }
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
                OnPropertyChanged(nameof(CanPlanUndo));
                OnPropertyChanged(nameof(CanApproveUndo));
                OnPropertyChanged(nameof(CanCreateCorrection));
                OnPropertyChanged(nameof(CanExportCapsule));
                OnPropertyChanged(nameof(CanCancelActiveWork));
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
                OnPropertyChanged(nameof(CanPlanUndo));
                OnPropertyChanged(nameof(CanApproveUndo));
                OnPropertyChanged(nameof(CanCreateCorrection));
                OnPropertyChanged(nameof(CanExportCapsule));
                OnPropertyChanged(nameof(CanCancelActiveWork));
            }
        }
    }

    public bool CanCreateSafeLab => IsReady && !IsBusy && !hasActiveGrant;

    public bool CanStartTeaching => IsReady && !IsBusy && hasActiveGrant && !isObserving;

    public bool CanStopTeaching => IsReady && !IsBusy && isObserving;

    public bool CanCompileSkill =>
        IsReady && !IsBusy && hasActiveGrant && hasCompilableTeaching &&
        !isObserving && SkillCard is null;

    public bool CanRehearseSkill =>
        IsReady && !IsBusy && hasActiveGrant && SkillCard is not null &&
        RehearsalPlan is null;

    public bool CanApproveAndExecute =>
        IsReady && !IsBusy && hasActiveGrant && RehearsalPlan is not null &&
        !productionAttempted;

    public bool CanPlanUndo =>
        IsReady && !IsBusy && hasActiveGrant && ExecutionReceipt is not null &&
        UndoPlan is null;

    public bool CanApproveUndo =>
        IsReady && !IsBusy && hasActiveGrant && UndoPlan is not null && !undoAttempted;

    public bool CanCreateCorrection =>
        IsReady && !IsBusy && hasActiveGrant && ExecutionReceipt is not null &&
        !correctionAttempted;

    public bool CanExportCapsule =>
        IsReady && !IsBusy && SkillCard is not null && CapsuleExport is null;

    public bool CanRevokeFolderGrant => hasActiveGrant;

    public bool CanCancelActiveWork => IsBusy;

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
                OnPropertyChanged(nameof(CanExportCapsule));
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
                OnPropertyChanged(nameof(CanPlanUndo));
                OnPropertyChanged(nameof(CanCreateCorrection));
            }
        }
    }

    public bool HasExecutionReceipt => ExecutionReceipt is not null;

    public UndoPlanViewModel? UndoPlan
    {
        get => undoPlan;
        private set
        {
            if (SetProperty(ref undoPlan, value))
            {
                OnPropertyChanged(nameof(HasUndoPlan));
                OnPropertyChanged(nameof(CanPlanUndo));
                OnPropertyChanged(nameof(CanApproveUndo));
            }
        }
    }

    public bool HasUndoPlan => UndoPlan is not null;

    public UndoReceiptViewModel? UndoReceipt
    {
        get => undoReceipt;
        private set
        {
            if (SetProperty(ref undoReceipt, value))
            {
                OnPropertyChanged(nameof(HasUndoReceipt));
            }
        }
    }

    public bool HasUndoReceipt => UndoReceipt is not null;

    public CapsuleExportViewModel? CapsuleExport
    {
        get => capsuleExport;
        private set
        {
            if (SetProperty(ref capsuleExport, value))
            {
                OnPropertyChanged(nameof(HasCapsuleExport));
                OnPropertyChanged(nameof(CanExportCapsule));
            }
        }
    }

    public bool HasCapsuleExport => CapsuleExport is not null;

    public CompanionBodyProjection CurrentBody
    {
        get => currentBody;
        private set
        {
            if (SetProperty(ref currentBody, value))
            {
                OnPropertyChanged(nameof(BodyAccessibleName));
            }
        }
    }

    public string BodyEvidenceReasonCode
    {
        get => bodyEvidenceReasonCode;
        private set
        {
            if (SetProperty(ref bodyEvidenceReasonCode, value))
            {
                OnPropertyChanged(nameof(BodyAccessibleName));
            }
        }
    }

    public string BodyEvidenceSummary
    {
        get => bodyEvidenceSummary;
        private set => SetProperty(ref bodyEvidenceSummary, value);
    }

    public string BodyAccessibleName =>
        $"Tooltail body. {Headline}. {CurrentBody.ReasonCode}. " +
        $"Evidence {BodyEvidenceReasonCode}.";

    public NormalizedAgentToolKind? LastAcceptedToolKind
    {
        get => lastAcceptedToolKind;
        private set => SetProperty(ref lastAcceptedToolKind, value);
    }

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
            SetBody(
                bodyFacts with
                {
                    HasFailed = !result.ReasonCode.EndsWith(
                        ".cancelled",
                        StringComparison.Ordinal),
                    IsPausedOrCancelled = result.ReasonCode.EndsWith(
                        ".cancelled",
                        StringComparison.Ordinal),
                },
                result.ReasonCode,
                "Startup did not produce a trusted persisted read model.");
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
        OnPropertyChanged(nameof(CanPlanUndo));
        OnPropertyChanged(nameof(CanApproveUndo));
        OnPropertyChanged(nameof(CanCreateCorrection));
        OnPropertyChanged(nameof(CanExportCapsule));
        OnPropertyChanged(nameof(CanRevokeFolderGrant));
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
        bool recoveryRequired = result.Recovery.Candidates.Count > 0;
        ExecutionSummaryStateRecord? latestExecution = workspace.Executions.Count == 0
            ? null
            : workspace.Executions[0];
        bool persistedFailure = latestExecution?.Status == PersistedExecutionStatus.Failed;
        bool cancelled = latestExecution?.Status == PersistedExecutionStatus.Cancelled;
        TeachingEpisodeSummaryStateRecord? latestTeaching =
            workspace.TeachingEpisodes.Count == 0
                ? null
                : workspace.TeachingEpisodes[0];
        bool needsInput = workspace.CurrentSkills.Any(static skill =>
                skill.Version.Lifecycle is SkillLifecycleState.Draft or
                    SkillLifecycleState.Approved) ||
            (workspace.CurrentSkills.Count == 0 &&
             latestTeaching is
             {
                 Status: PersistedTeachingEpisodeStatus.Reconciled,
                 EvidenceStatus: PersistedTeachingEvidenceStatus.Complete,
             });
        bool receiptReady = latestExecution is
        {
            Status: PersistedExecutionStatus.Verified,
            HasReceipt: true,
        };
        bool permissionRevoked = !hasActiveGrant && workspace.Grants.Any(static grant =>
            grant.Grant.State == ResourceGrantState.Revoked);
        folderGrantRevoked = permissionRevoked;
        SetBody(
            new CompanionActivityFacts(
                HasVisibleScope: hasActiveGrant,
                NeedsInput: needsInput,
                HasCompletedReceipt: receiptReady,
                HasFailed: recoveryRequired || persistedFailure,
                IsPausedOrCancelled: cancelled,
                IsPermissionRevoked: permissionRevoked),
            recoveryRequired ? "startup.recovery_required" : result.ReasonCode,
            recoveryRequired
                ? "Durable journal truth requires inspect-first recovery; nothing was replayed."
                : "The body was reconstructed from the bounded SQLite workspace read model.");
    }

    public void BeginAction(string message, NormalizedAgentToolKind toolKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!Enum.IsDefined(toolKind))
        {
            throw new ArgumentOutOfRangeException(nameof(toolKind));
        }

        IsBusy = true;
        LastActionMessage = message;
        LastAcceptedToolKind = toolKind;
        pendingInputBeforeAction = bodyFacts.NeedsInput;
        SetBody(
            bodyFacts with
            {
                IsObserving = false,
                IsWorking = true,
                ToolKind = toolKind,
                NeedsInput = false,
                IsBlocked = false,
                HasFailed = false,
                IsPausedOrCancelled = false,
                IsPermissionRevoked = folderGrantRevoked,
                IsDisconnected = false,
            },
            "activity.accepted",
            $"An explicit bounded {toolKind.ToString().ToLowerInvariant()} workflow action is active; presentation creates no authority.");
    }

    public void CompleteAction(string reasonCode, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ReasonCode = reasonCode;
        IsBusy = false;
        LastActionMessage = message;
        if (bodyFacts.IsWorking)
        {
            SetBody(
                bodyFacts with
                {
                    IsWorking = false,
                    ToolKind = null,
                    IsPausedOrCancelled = reasonCode.EndsWith(
                        ".cancelled",
                        StringComparison.Ordinal),
                },
                reasonCode,
                "The active workflow ended without an optimistic completion pose.");
        }
    }

    public void FailAction(string reasonCode, string message)
    {
        CompleteAction(reasonCode, message);
        bool cancelled = reasonCode.EndsWith(".cancelled", StringComparison.Ordinal);
        SetBody(
            bodyFacts with
            {
                IsWorking = false,
                ToolKind = null,
                HasFailed = !cancelled,
                IsPausedOrCancelled = cancelled,
            },
            reasonCode,
            cancelled
                ? "The bounded workflow was cancelled and cannot appear active."
                : "The bounded workflow stopped without a verified success result.");
    }

    public void ApplySafeLab(SafeLabGrantResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.IsSuccess || result.Grant is null || result.CanonicalLabPath is null)
        {
            throw new ArgumentException("Only a successful safe lab result can be applied.", nameof(result));
        }

        hasActiveGrant = true;
        folderGrantRevoked = false;
        LabPath = result.CanonicalLabPath;
        GrantState = $"Safe lab grant {result.Grant.Id.Value:D}: active; " +
            $"{result.Grant.Capabilities.Count.ToString(CultureInfo.InvariantCulture)} exact actions; " +
            $"expires {result.Grant.ExpiresAt:O}.";
        Headline = "Safe lab granted — ready to capture a teaching baseline";
        CompleteAction(
            result.ReasonCode,
            "Created three synthetic invoice PDFs in a new Tooltail-owned folder. " +
            "No existing file was overwritten or removed.");
        SetSettledBody(
            result.ReasonCode,
            "The exact active ResourceGrant was persisted; it creates file scope but no execution approval.");
        OnPropertyChanged(nameof(CanCreateSafeLab));
        OnPropertyChanged(nameof(CanStartTeaching));
        OnPropertyChanged(nameof(CanRevokeFolderGrant));
    }

    public void ApplyRestoredSafeLab(SafeLabGrantResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.IsSuccess || result.Grant is null || result.CanonicalLabPath is null)
        {
            throw new ArgumentException("Only a restored safe lab can be applied.", nameof(result));
        }

        hasActiveGrant = true;
        folderGrantRevoked = false;
        LabPath = result.CanonicalLabPath;
        OnPropertyChanged(nameof(CanCreateSafeLab));
        OnPropertyChanged(nameof(CanStartTeaching));
        OnPropertyChanged(nameof(CanRevokeFolderGrant));
    }

    public void ApplyFolderGrantRevocation(
        SafeLabGrantResult result,
        bool operationStillStopping)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.IsSuccess || result.Grant is not
            {
                State: ResourceGrantState.Revoked,
            })
        {
            if (operationStillStopping)
            {
                ReasonCode = result.ReasonCode;
                LastActionMessage =
                    $"Folder grant revocation did not persist: {result.ReasonCode}. The active operation remains under its existing authority checks.";
            }
            else
            {
                FailAction(
                    result.ReasonCode,
                    $"Folder grant revocation did not persist: {result.ReasonCode}.");
            }

            return;
        }

        hasActiveGrant = false;
        folderGrantRevoked = true;
        GrantState = $"Safe lab grant {result.Grant.Id.Value:D}: revoked at " +
            $"{result.Grant.RevokedAt:O}; reason {result.Grant.RevocationReason}.";
        Headline = "Folder authority revoked — active and future file work is stopped";
        if (operationStillStopping)
        {
            ReasonCode = result.ReasonCode;
            LastActionMessage =
                "The exact ResourceGrant is durably revoked. Cooperative cancellation is still stopping the active operation; the lab grant cannot authorize another effect.";
        }
        else
        {
            CompleteAction(
                result.ReasonCode,
                "The exact ResourceGrant is durably revoked. The lab remains untouched, but it cannot be planned, rehearsed, executed, or undone without a new grant.");
        }
        SetSettledBody(
            result.ReasonCode,
            "Durable current grant state is revoked and interruptively outranks background presentation.",
            isPermissionRevoked: true);
        OnPropertyChanged(nameof(CanCreateSafeLab));
        OnPropertyChanged(nameof(CanStartTeaching));
        OnPropertyChanged(nameof(CanStopTeaching));
        OnPropertyChanged(nameof(CanCompileSkill));
        OnPropertyChanged(nameof(CanRehearseSkill));
        OnPropertyChanged(nameof(CanApproveAndExecute));
        OnPropertyChanged(nameof(CanPlanUndo));
        OnPropertyChanged(nameof(CanApproveUndo));
        OnPropertyChanged(nameof(CanCreateCorrection));
        OnPropertyChanged(nameof(CanRevokeFolderGrant));
    }

    public void ReportControlStopRequested(bool pause)
    {
        string reasonCode = pause
            ? "control.pause_requested"
            : "control.cancel_requested";
        LastActionMessage = pause
            ? "Safe pause requested. The active operation is cancelling at its next cooperative boundary and will not auto-resume."
            : "Cancellation requested. The active operation is stopping at its next cooperative boundary.";
        SetBody(
            bodyFacts with
            {
                IsPausedOrCancelled = true,
            },
            reasonCode,
            pause
                ? "Pause is implemented as fail-safe cooperative cancellation; potentially mutable work is never blindly resumed."
                : "The active bounded operation received a cooperative cancellation request.");
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
        SetSettledBody(
            result.ReasonCode,
            result.IsSuccess
                ? "The persisted baseline and active bounded observation session select observing."
                : "Observation did not start from a complete authoritative baseline.",
            isObserving: result.IsSuccess,
            hasFailed: !result.IsSuccess);
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
        SetSettledBody(
            result.ReasonCode,
            result.IsSuccess
                ? "Authoritative baseline/final reconciliation committed safe examples and now requires an explicit compile action."
                : "Teaching evidence was incomplete, unsupported, or irreconcilable.",
            needsInput: result.IsSuccess,
            hasFailed: !result.IsSuccess);
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
            SetSettledBody(
                result.ReasonCode,
                "Closed typed compiler questions require explicit user input; no SkillSpec or authority exists yet.",
                needsInput: true);
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
            SetSettledBody(
                result.ReasonCode,
                "The immutable Draft is persisted and requires inspection plus rehearsal before approval.",
                needsInput: true);
            return;
        }

        Headline = "No safe SkillSpec candidate";
        CompleteAction(result.ReasonCode, $"Compilation stopped: {result.ReasonCode}.");
        SetSettledBody(
            result.ReasonCode,
            "The deterministic compiler did not commit a safe executable candidate.",
            hasFailed: true);
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
            SetSettledBody(
                result.ReasonCode,
                "Verified rehearsal committed an exact unapproved production plan that now requires explicit user approval.",
                needsInput: true);
            return;
        }

        Headline = result.Rehearsal?.Cleanup is { IsSuccess: false }
            ? "Rehearsal stopped with an owned cleanup residual"
            : "Rehearsal did not pass";
        CompleteAction(
            result.ReasonCode,
            $"No production approval is available: {result.ReasonCode}.");
        SetSettledBody(
            result.ReasonCode,
            "Rehearsal did not produce a verified shared-executor result and cannot claim readiness.",
            hasFailed: true);
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
            SetSettledBody(
                result.ReasonCode,
                "A verified durable production receipt exists for the exact approved plan.",
                hasCompletedReceipt: true);
        }
        else
        {
            Headline = result.Execution?.Receipt is not null
                ? "Execution verified but persisted lifecycle needs inspection"
                : "Production execution stopped safely";
            CompleteAction(
                result.ReasonCode,
                $"Production did not reach a fully persisted success state: {result.ReasonCode}.");
            SetSettledBody(
                result.ReasonCode,
                "Production lacks a fully verified durable success projection and cannot show completion.",
                hasFailed: true);
        }

        OnPropertyChanged(nameof(CanApproveAndExecute));
        OnPropertyChanged(nameof(CanPlanUndo));
    }

    public void ApplyUndoPlanning(UndoPlanningWorkflowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsSuccess && result.Plan is not null)
        {
            UndoPlan = UndoPlanViewModel.From(result.Plan);
            RecoveryState =
                $"Exact recovery preview ready; {result.OperationCount.ToString(CultureInfo.InvariantCulture)} operation(s); not approved.";
            Headline = "Undo preview ready — inspect the new recovery fingerprint";
            CompleteAction(
                result.ReasonCode,
                "The preview was derived only from the verified receipt, exact journal, and current authoritative snapshot. Nothing was rolled back.");
            SetSettledBody(
                result.ReasonCode,
                "The canonical recovery preview is unapproved and requires a separate explicit decision.",
                needsInput: true,
                hasCompletedReceipt: true);
            return;
        }

        Headline = "Undo preview refused";
        CompleteAction(
            result.ReasonCode,
            $"No recovery approval is available: {result.ReasonCode}.");
        SetSettledBody(
            result.ReasonCode,
            "Current state did not support a safe recovery preview.",
            hasFailed: true);
        OnPropertyChanged(nameof(CanPlanUndo));
    }

    public void ApplyUndoExecution(UndoExecutionWorkflowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        undoAttempted = true;
        if (result.Execution?.Receipt is not null)
        {
            UndoReceipt = UndoReceiptViewModel.From(result.Execution.Receipt);
        }

        if (result.IsSuccess && result.Execution?.IsVerified == true)
        {
            RecoveryState =
                $"Undo {result.Execution.Status}; " +
                $"{result.Execution.VerifiedSteps.Count.ToString(CultureInfo.InvariantCulture)} verified recovery step(s); no residual effects.";
            ExecutionState = "Production receipt retained; separately approved Undo is verified.";
            Headline = "Undo verified — the original safe-lab tree is restored";
            CompleteAction(
                result.ReasonCode,
                "The undo-only approval was consumed once, the recovery journal was verified, and the original journal now links to the distinct recovery execution.");
            SetSettledBody(
                result.ReasonCode,
                "A distinct verified recovery receipt proves the separately approved Undo result.",
                hasCompletedReceipt: true);
        }
        else
        {
            RecoveryState = result.Execution?.ResidualEffectCodes.Count > 0
                ? string.Join("; ", result.Execution.ResidualEffectCodes)
                : $"Undo stopped: {result.ReasonCode}.";
            Headline = result.Execution is { ResidualEffectCodes.Count: > 0 }
                ? "Undo stopped with residual state requiring inspection"
                : "Undo stopped safely";
            CompleteAction(
                result.ReasonCode,
                $"Undo did not reach verified restoration: {result.ReasonCode}.");
            SetSettledBody(
                result.ReasonCode,
                "Undo lacks a verified restoration result and may require inspection.",
                hasFailed: true);
        }

        OnPropertyChanged(nameof(CanApproveUndo));
    }

    public void ApplyCorrection(SkillCorrectionWorkflowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        correctionAttempted = true;
        if (result.IsSuccess &&
            result.CorrectedCompilation?.Card is not null &&
            result.Correction?.Specification is not null &&
            result.Correction.SemanticDiff is not null)
        {
            SkillCard = result.CorrectedCompilation.Card;
            RehearsalPlan = null;
            productionAttempted = false;
            SkillState =
                $"{result.Correction.Specification.Name} v" +
                $"{result.Correction.Specification.Version.ToString(CultureInfo.InvariantCulture)} (Draft; parent v" +
                $"{result.Correction.Specification.Provenance.ParentVersion!.Value.ToString(CultureInfo.InvariantCulture)})";
            Headline = "Corrected Draft v2 saved — semantic diff is causal and rehearsal is required again";
            CompleteAction(
                result.ReasonCode,
                $"Changed executable field group(s): {string.Join(", ", result.Correction.SemanticDiff.ChangedFields)}. " +
                "The target edge case changed, but no approval or execution authority was created.");
            SetSettledBody(
                result.ReasonCode,
                "The immutable corrected Draft changed executable semantics and requires new rehearsal and approval.",
                needsInput: true,
                hasCompletedReceipt: true);
        }
        else
        {
            Headline = "Correction did not produce a safe executable change";
            CompleteAction(
                result.ReasonCode,
                $"No corrected version was persisted: {result.ReasonCode}.");
            SetSettledBody(
                result.ReasonCode,
                "Correction did not commit a causally changed safe skill version.",
                hasFailed: true);
        }

        OnPropertyChanged(nameof(CanCreateCorrection));
    }

    public void ApplyCapsuleExport(CapsuleExportWorkflowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsSuccess &&
            result.CanonicalPath is not null &&
            result.Preview is
            {
                CreatesAuthority: false,
                CanImport: false,
                SkillsRequireRebind: true,
            })
        {
            CapsuleExport = new CapsuleExportViewModel(
                result.CanonicalPath,
                result.ByteCount,
                result.SkillVersionCount,
                result.Preview.ReasonCode,
                result.Preview.CreatesAuthority,
                result.Preview.CanImport,
                result.Preview.SkillsRequireRebind);
            Headline = "Companion capsule exported locally — import remains authority-free and disabled";
            CompleteAction(
                result.ReasonCode,
                "Validated immutable SkillSpecs and bounded evidence before writing. The capsule contains no live grant, approval, path, journal, credential, or import authority.");
            SetSettledBody(
                result.ReasonCode,
                "A validated local capsule exists, but any pending corrected Draft still outranks its output receipt.",
                needsInput: pendingInputBeforeAction,
                hasCompletedReceipt: true);
        }
        else
        {
            Headline = "Capsule export stopped before a trusted handoff";
            CompleteAction(
                result.ReasonCode,
                $"Capsule export did not complete: {result.ReasonCode}.");
            SetSettledBody(
                result.ReasonCode,
                "Capsule export did not produce a validated local artifact.",
                hasFailed: true);
        }

        OnPropertyChanged(nameof(CanExportCapsule));
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

    private void SetSettledBody(
        string evidenceReasonCode,
        string evidenceSummary,
        bool isObserving = false,
        bool needsInput = false,
        bool hasCompletedReceipt = false,
        bool hasFailed = false,
        bool isPausedOrCancelled = false,
        bool isPermissionRevoked = false,
        bool isBlocked = false)
    {
        bool cancelled = isPausedOrCancelled || evidenceReasonCode.EndsWith(
            ".cancelled",
            StringComparison.Ordinal);
        SetBody(
            new CompanionActivityFacts(
                HasVisibleScope: hasActiveGrant,
                IsObserving: isObserving,
                NeedsInput: needsInput,
                IsBlocked: isBlocked,
                HasCompletedReceipt: hasCompletedReceipt,
                HasFailed: hasFailed && !cancelled,
                IsPausedOrCancelled: cancelled,
                IsPermissionRevoked: isPermissionRevoked || folderGrantRevoked),
            evidenceReasonCode,
            evidenceSummary);
    }

    private void SetBody(
        CompanionActivityFacts facts,
        string evidenceReasonCode,
        string evidenceSummary)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceReasonCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceSummary);
        bodyFacts = facts;
        CurrentBody = CompanionActivityProjector.Project(facts);
        BodyEvidenceReasonCode = evidenceReasonCode;
        BodyEvidenceSummary = evidenceSummary;
    }

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

public sealed record UndoPlanViewModel(
    string PlanId,
    string Fingerprint,
    string OriginalExecutionId,
    string OriginalPlanFingerprint,
    string ExpiresUtc,
    IReadOnlyList<UndoOperationViewModel> Operations)
{
    public static UndoPlanViewModel From(RecoveryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new UndoPlanViewModel(
            plan.Definition.Id.Value.ToString("D"),
            plan.Fingerprint.Value,
            plan.Definition.OriginalExecutionId.Value.ToString("D"),
            plan.Definition.OriginalPlanFingerprint.Value,
            plan.Definition.ExpiresUtc.ToString("O", CultureInfo.InvariantCulture),
            plan.Definition.Operations.Select(UndoOperationViewModel.From).ToArray());
    }
}

public sealed record UndoOperationViewModel(
    int Sequence,
    int OriginalStepSequence,
    string Primitive,
    string Source,
    string Destination,
    string ExpectedIdentity)
{
    public static UndoOperationViewModel From(PlannedRecoveryOperation operation) =>
        new(
            operation.Sequence,
            operation.OriginalStepSequence,
            operation.Primitive.ToString(),
            operation.SourceRelativePath,
            operation.DestinationRelativePath ?? "(remove exact created entry)",
            operation.ExpectedSource.EntryIdentity);
}

public sealed record UndoReceiptViewModel(
    string ReceiptId,
    string ExecutionId,
    string PlanFingerprint,
    string OriginalExecutionId,
    string OriginalPlanFingerprint,
    string CompletedUtc,
    IReadOnlyList<UndoReceiptStepViewModel> VerifiedSteps)
{
    public static UndoReceiptViewModel From(RecoveryExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return new UndoReceiptViewModel(
            receipt.Id.Value.ToString("D"),
            receipt.ExecutionId.Value.ToString("D"),
            receipt.PlanFingerprint.Value,
            receipt.OriginalExecutionId.Value.ToString("D"),
            receipt.OriginalPlanFingerprint.Value,
            receipt.CompletedUtc.ToString("O", CultureInfo.InvariantCulture),
            receipt.VerifiedSteps.Select(UndoReceiptStepViewModel.From).ToArray());
    }
}

public sealed record UndoReceiptStepViewModel(
    int Sequence,
    int OriginalStepSequence,
    string Primitive,
    string Source,
    string Destination,
    string RecoveredIdentity)
{
    public static UndoReceiptStepViewModel From(
        VerifiedRecoveryStepEvidence evidence) =>
        new(
            evidence.StepSequence,
            evidence.OriginalStepSequence,
            evidence.Primitive.ToString(),
            evidence.SourceRelativePath,
            evidence.DestinationRelativePath ?? "(removed)",
            evidence.RecoveredEntry.EntryIdentity);
}

public sealed record CapsuleExportViewModel(
    string CanonicalPath,
    int ByteCount,
    int SkillVersionCount,
    string PreviewReasonCode,
    bool CreatesAuthority,
    bool CanImport,
    bool SkillsRequireRebind);
