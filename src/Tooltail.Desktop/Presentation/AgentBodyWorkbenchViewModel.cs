using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Tooltail.Adapters.AgentEvents.Simulator;
using Tooltail.Application.Abstractions;
using Tooltail.Domain.Agents;

namespace Tooltail.Desktop.Presentation;

public sealed class AgentBodyWorkbenchViewModel : INotifyPropertyChanged
{
    private readonly CompanionBodyProjection resetBody = new(
        CompanionBodyState.HomeIdle,
        ToolKind: null,
        ParallelUnitCount: 0,
        "body.home_idle");
    private readonly string scopeSummary =
        "Simulator only — no window is bound and no WindowLease is active.";
    private readonly string authoritySummary =
        "Normalized events create no file grant, approval, lease, or learned-skill authority.";
    private readonly bool isDevelopmentPanelVisible;
    private SimulatorPlaybackDocument? document;
    private SimulatorTraceChoice? selectedTrace;
    private PlaybackSpeedChoice selectedSpeed;
    private CompanionBodyProjection currentBody;
    private int currentFrameIndex;
    private int loadVersion;
    private bool isLoading;
    private bool isPlaying;
    private string adapterStatus = "Not loaded";
    private string adapterReasonCode = "simulator.not_loaded";
    private string headline = "At home";
    private string explanation = "No committed run event is active.";
    private string evidenceSummary = "Playback is reset before the first normalized event.";
    private string activeToolsSummary = "none";
    private int pendingQuestionCount;
    private int activeSubagentCount;

    public AgentBodyWorkbenchViewModel(IClock clock, bool reducedMotion)
    {
        ArgumentNullException.ThrowIfNull(clock);
        TraceChoices = SimulatorTraceCatalog.All
            .Select(static trace => new SimulatorTraceChoice(trace.Name, trace.Description))
            .ToArray();
        SpeedChoices =
        [
            new PlaybackSpeedChoice("0.5×", TimeSpan.FromSeconds(2)),
            new PlaybackSpeedChoice("1×", TimeSpan.FromSeconds(1)),
            new PlaybackSpeedChoice("2×", TimeSpan.FromMilliseconds(500)),
            new PlaybackSpeedChoice("4×", TimeSpan.FromMilliseconds(250)),
        ];
        selectedSpeed = SpeedChoices[1];
        currentBody = resetBody;
        ReducedMotion = reducedMotion;
        SessionStarted = $"Simulator session started {clock.UtcNow:O}";
#if DEBUG
        isDevelopmentPanelVisible = true;
#else
        isDevelopmentPanelVisible = false;
#endif
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<SimulatorTraceChoice> TraceChoices { get; }

    public IReadOnlyList<PlaybackSpeedChoice> SpeedChoices { get; }

    public ObservableCollection<AgentTimelineRow> Timeline { get; } = [];

    public SimulatorTraceChoice? SelectedTrace
    {
        get => selectedTrace;
        set => SetProperty(ref selectedTrace, value);
    }

    public PlaybackSpeedChoice SelectedSpeed
    {
        get => selectedSpeed;
        set
        {
            if (value is not null && SetProperty(ref selectedSpeed, value))
            {
                OnPropertyChanged(nameof(PlaybackInterval));
            }
        }
    }

    public TimeSpan PlaybackInterval => SelectedSpeed.Interval;

    public CompanionBodyProjection CurrentBody
    {
        get => currentBody;
        private set
        {
            if (SetProperty(ref currentBody, value))
            {
                OnPropertyChanged(nameof(CurrentStateCode));
                OnPropertyChanged(nameof(CurrentParameter));
                OnPropertyChanged(nameof(CurrentReasonCode));
                OnPropertyChanged(nameof(AccessibleBodyName));
            }
        }
    }

    public string CurrentStateCode => StateCode(CurrentBody.State);

    public string CurrentParameter => ParameterText(CurrentBody);

    public string CurrentReasonCode => CurrentBody.ReasonCode;

    public string Headline
    {
        get => headline;
        private set
        {
            if (SetProperty(ref headline, value))
            {
                OnPropertyChanged(nameof(AccessibleBodyName));
            }
        }
    }

    public string Explanation
    {
        get => explanation;
        private set
        {
            if (SetProperty(ref explanation, value))
            {
                OnPropertyChanged(nameof(AccessibleBodyName));
            }
        }
    }

    public string EvidenceSummary
    {
        get => evidenceSummary;
        private set => SetProperty(ref evidenceSummary, value);
    }

    public string AdapterStatus
    {
        get => adapterStatus;
        private set => SetProperty(ref adapterStatus, value);
    }

    public string AdapterReasonCode
    {
        get => adapterReasonCode;
        private set => SetProperty(ref adapterReasonCode, value);
    }

    public string AccessibleBodyName =>
        $"Tooltail body: {Headline}. {Explanation} Reason {CurrentReasonCode}.";

    public string CurrentStepLabel => document is null
        ? "Step 0 of 0"
        : $"Step {currentFrameIndex.ToString(CultureInfo.InvariantCulture)} of " +
            $"{(document.Frames.Count - 1).ToString(CultureInfo.InvariantCulture)}";

    public string ActiveToolsSummary
    {
        get => activeToolsSummary;
        private set => SetProperty(ref activeToolsSummary, value);
    }

    public int PendingQuestionCount
    {
        get => pendingQuestionCount;
        private set => SetProperty(ref pendingQuestionCount, value);
    }

    public int ActiveSubagentCount
    {
        get => activeSubagentCount;
        private set => SetProperty(ref activeSubagentCount, value);
    }

    public string PlayButtonLabel => IsPlaying ? "Pause playback" : "Play trace";

    public bool CanStepForward =>
        document is not null && currentFrameIndex + 1 < document.Frames.Count;

    public bool IsLoading
    {
        get => isLoading;
        private set => SetProperty(ref isLoading, value);
    }

    public bool IsPlaying
    {
        get => isPlaying;
        private set
        {
            if (SetProperty(ref isPlaying, value))
            {
                OnPropertyChanged(nameof(PlayButtonLabel));
            }
        }
    }

    public bool ReducedMotion { get; }

    public string SessionStarted { get; }

    public string ScopeSummary => scopeSummary;

    public string AuthoritySummary => authoritySummary;

    public bool IsDevelopmentPanelVisible => isDevelopmentPanelVisible;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (TraceChoices.Count == 0)
        {
            return;
        }

        SelectedTrace ??= TraceChoices[0];
        await SelectTraceAsync(SelectedTrace, cancellationToken);
    }

    public async Task SelectTraceAsync(
        SimulatorTraceChoice choice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (!SimulatorTraceCatalog.TryGet(choice.Name, out SimulatorTraceDefinition? trace))
        {
            throw new ArgumentException("The simulator trace is not in the fixed catalog.", nameof(choice));
        }

        int requestedVersion = ++loadVersion;
        IsLoading = true;
        SetPlaybackActive(false);
        try
        {
            SimulatorPlaybackDocument loaded = await SimulatorPlaybackDocument.CreateAsync(
                trace,
                cancellationToken);
            if (requestedVersion != loadVersion)
            {
                return;
            }

            document = loaded;
            SelectedTrace = choice;
            AdapterStatus = loaded.StreamStatus.ToString();
            AdapterReasonCode = loaded.StreamReasonCode;
            Timeline.Clear();
            foreach (SimulatorPlaybackFrame frame in loaded.Frames.Skip(1))
            {
                Timeline.Add(AgentTimelineRow.FromFrame(frame));
            }

            Reset();
        }
        finally
        {
            if (requestedVersion == loadVersion)
            {
                IsLoading = false;
            }
        }
    }

    public bool StepForward()
    {
        if (document is null || currentFrameIndex + 1 >= document.Frames.Count)
        {
            SetPlaybackActive(false);
            return false;
        }

        currentFrameIndex++;
        ApplyFrame(document.Frames[currentFrameIndex]);
        if (currentFrameIndex + 1 >= document.Frames.Count)
        {
            SetPlaybackActive(false);
        }

        return true;
    }

    public void Reset()
    {
        SetPlaybackActive(false);
        currentFrameIndex = 0;
        ApplyFrame(document?.Frames[0] ?? new SimulatorPlaybackFrame(
            Index: 0,
            InputLine: null,
            Sequence: null,
            EventType: null,
            Disposition: null,
            AdapterSynthesized: true,
            resetBody,
            NormalizedEvent: null,
            ActiveToolKinds: [],
            PendingQuestionCount: 0,
            ActiveSubagentCount: 0,
            "simulator.playback_reset"));
    }

    public void SetPlaybackActive(bool active)
    {
        IsPlaying = active && CanStepForward;
    }

    private void ApplyFrame(SimulatorPlaybackFrame frame)
    {
        CurrentBody = frame.Body;
        Headline = HeadlineText(frame.Body);
        Explanation = ExplanationText(frame.Body);
        EvidenceSummary = EvidenceText(frame);
        ActiveToolsSummary = frame.ActiveToolKinds.Count == 0
            ? "none"
            : string.Join(", ", frame.ActiveToolKinds.Select(
                static kind => kind.ToString().ToLowerInvariant()));
        PendingQuestionCount = frame.PendingQuestionCount;
        ActiveSubagentCount = frame.ActiveSubagentCount;
        OnPropertyChanged(nameof(CurrentStepLabel));
        OnPropertyChanged(nameof(CanStepForward));
    }

    private static string EvidenceText(SimulatorPlaybackFrame frame)
    {
        if (frame.EventType is not null)
        {
            return $"Normalized event {frame.Sequence}: {frame.EventType} " +
                $"({frame.Disposition}) selected {StateCode(frame.Body.State)} " +
                $"because {frame.EvidenceReasonCode}.";
        }

        return frame.EvidenceReasonCode == "simulator.playback_reset"
            ? "Playback is reset before the first normalized event."
            : $"The adapter boundary selected {StateCode(frame.Body.State)} because " +
                $"{frame.EvidenceReasonCode}; no raw payload was retained.";
    }

    private static string HeadlineText(CompanionBodyProjection body) =>
        body.State switch
        {
            CompanionBodyState.HomeIdle => "At home",
            CompanionBodyState.ScopedIdle => "Context is visible",
            CompanionBodyState.Observing => "Observing context",
            CompanionBodyState.Working when body.ToolKind is not null =>
                $"Working with {body.ToolKind}",
            CompanionBodyState.Working => "Working",
            CompanionBodyState.ParallelWork =>
                $"{body.ParallelUnitCount} work units active",
            CompanionBodyState.NeedsInput => "Needs your input",
            CompanionBodyState.Blocked => "Blocked",
            CompanionBodyState.CompletedReceipt => "Receipt ready",
            CompanionBodyState.Failed => "Run failed",
            CompanionBodyState.PausedOrCancelled when
                body.ReasonCode == "body.cancelled" => "Run cancelled",
            CompanionBodyState.PausedOrCancelled => "Run paused",
            CompanionBodyState.PermissionRevoked => "Permission revoked",
            CompanionBodyState.Disconnected => "Adapter disconnected",
            _ => "Unknown body state",
        };

    private static string ExplanationText(CompanionBodyProjection body) =>
        body.State switch
        {
            CompanionBodyState.HomeIdle => "No committed run event is active.",
            CompanionBodyState.ScopedIdle =>
                "A visible context exists, but it grants no mutation authority.",
            CompanionBodyState.Observing =>
                "Observation is active; observation does not grant mutation authority.",
            CompanionBodyState.Working when body.ToolKind is not null =>
                $"One bounded {body.ToolKind} work unit is active.",
            CompanionBodyState.Working => "The normalized run is active.",
            CompanionBodyState.ParallelWork =>
                $"Exactly {body.ParallelUnitCount} bounded work units are active.",
            CompanionBodyState.NeedsInput =>
                "An exact pending question outranks any background work.",
            CompanionBodyState.Blocked =>
                "The run reported a non-terminal block and cannot claim progress.",
            CompanionBodyState.CompletedReceipt =>
                "Completion was committed and a result receipt is ready to inspect.",
            CompanionBodyState.Failed =>
                "Failure outranks tools, observation, and completion presentation.",
            CompanionBodyState.PausedOrCancelled =>
                "Paused or cancelled work cannot continue to appear active.",
            CompanionBodyState.PermissionRevoked =>
                "Revocation outranks every active background tool.",
            CompanionBodyState.Disconnected =>
                "The event stream is no longer trustworthy, so working presentation stopped.",
            _ => "The state is outside the closed presentation vocabulary.",
        };

    internal static string StateCode(CompanionBodyState state) =>
        state switch
        {
            CompanionBodyState.HomeIdle => "home_idle",
            CompanionBodyState.ScopedIdle => "scoped_idle",
            CompanionBodyState.Observing => "observing",
            CompanionBodyState.Working => "working",
            CompanionBodyState.ParallelWork => "parallel_work",
            CompanionBodyState.NeedsInput => "needs_input",
            CompanionBodyState.Blocked => "blocked",
            CompanionBodyState.CompletedReceipt => "completed_receipt",
            CompanionBodyState.Failed => "failed",
            CompanionBodyState.PausedOrCancelled => "paused_or_cancelled",
            CompanionBodyState.PermissionRevoked => "permission_revoked",
            CompanionBodyState.Disconnected => "disconnected",
            _ => "unknown",
        };

    internal static string ParameterText(CompanionBodyProjection body) =>
        body.State switch
        {
            CompanionBodyState.Working when body.ToolKind is not null =>
                $"tool_kind={body.ToolKind.ToString()!.ToLowerInvariant()}",
            CompanionBodyState.ParallelWork =>
                $"count={body.ParallelUnitCount.ToString(CultureInfo.InvariantCulture)}",
            _ => "none",
        };

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record SimulatorTraceChoice(string Name, string Description)
{
    public string DisplayName => Name.Replace('-', ' ');
}

public sealed record PlaybackSpeedChoice(string Label, TimeSpan Interval);

public sealed record AgentTimelineRow(
    string EventId,
    string RunId,
    string Sequence,
    string OccurredUtc,
    string Source,
    string EventType,
    string Severity,
    string Data,
    string Disposition,
    string BodyState,
    string Parameter,
    string ReasonCode,
    string Origin)
{
    public static AgentTimelineRow FromFrame(SimulatorPlaybackFrame frame) =>
        new(
            frame.NormalizedEvent?.Id.Value.ToString("D") ?? "—",
            frame.NormalizedEvent?.RunId.Value.ToString("D") ?? "—",
            frame.Sequence?.ToString(CultureInfo.InvariantCulture) ?? "—",
            frame.NormalizedEvent?.OccurredUtc.ToString("O", CultureInfo.InvariantCulture) ?? "—",
            frame.NormalizedEvent?.Source.ToString() ?? "Adapter",
            frame.EventType?.ToString() ?? "AdapterStatus",
            frame.NormalizedEvent?.Severity.ToString() ?? "Error",
            DataText(frame.NormalizedEvent?.Data),
            frame.Disposition?.ToString() ?? "Synthesized",
            AgentBodyWorkbenchViewModel.StateCode(frame.Body.State),
            AgentBodyWorkbenchViewModel.ParameterText(frame.Body),
            frame.EvidenceReasonCode,
            frame.AdapterSynthesized ? "adapter" : "normalized");

    private static string DataText(NormalizedAgentEventData? data)
    {
        if (data is null)
        {
            return "none";
        }

        List<string> fields = [];
        Add(fields, "tool_kind", data.ToolKind?.ToString().ToLowerInvariant());
        Add(fields, "tool_call_id", data.ToolCallId);
        Add(fields, "question_id", data.QuestionId);
        Add(fields, "subagent_id", data.SubagentId);
        Add(fields, "display_label", data.DisplayLabel);
        Add(fields, "status_code", data.StatusCode);
        Add(
            fields,
            "progress",
            data.Progress?.ToString(CultureInfo.InvariantCulture));
        Add(
            fields,
            "parallel_unit_count",
            data.ParallelUnitCount?.ToString(CultureInfo.InvariantCulture));
        return fields.Count == 0 ? "none" : string.Join("; ", fields);
    }

    private static void Add(List<string> fields, string name, string? value)
    {
        if (value is not null)
        {
            fields.Add($"{name}={value}");
        }
    }
}
