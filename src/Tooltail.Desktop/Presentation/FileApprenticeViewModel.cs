using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Tooltail.Application.Abstractions;
using Tooltail.Application.FileSkills;
using Tooltail.Domain.Permissions;

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
    private string lastActionMessage = "Waiting for local SQLite initialization.";
    private bool isReady;
    private bool isFirstRun;
    private bool isBusy;

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
            }
        }
    }

    public bool CanCreateSafeLab => IsReady && !IsBusy;

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
