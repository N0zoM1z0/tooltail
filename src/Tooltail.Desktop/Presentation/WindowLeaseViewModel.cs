using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using Tooltail.Application.Windows;
using Tooltail.Contracts.Json;
using Tooltail.Domain.Agents;
using Tooltail.Domain.Windows;

namespace Tooltail.Desktop.Presentation;

public sealed class WindowLeaseViewModel : INotifyPropertyChanged
{
    public const string ContextDisclosure =
        "Window position limits Tooltail's context. It is not an operating-system sandbox.";

    public const string AuthorityDisclosure =
        "A WindowLease grants no file, UI, network, shell, model, or process action. " +
        "Actual actions require a separate exact ResourceGrant and approval.";

    private WindowBindingSnapshot snapshot = WindowBindingSnapshot.AtHome;
    private WindowTargetChoice? selectedTarget;
    private bool isRefreshingTargets;
    private string lastActionMessage = "Ready. No window context is bound.";
    private readonly bool reducedMotion = !SystemParameters.ClientAreaAnimation;
    private readonly FileApprenticeViewModel fileApprentice;

    public WindowLeaseViewModel(FileApprenticeViewModel fileApprentice)
    {
        ArgumentNullException.ThrowIfNull(fileApprentice);
        this.fileApprentice = fileApprentice;
        fileApprentice.PropertyChanged += OnFileApprenticePropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<WindowTargetChoice> EligibleTargets { get; } = [];

    public WindowBindingSnapshot Snapshot => snapshot;

    public WindowBindingMode Mode => snapshot.Mode;

    public string ModeCode => snapshot.Mode.ToString();

    public string Headline => snapshot.Mode switch
    {
        WindowBindingMode.Home => "At home — no window context",
        WindowBindingMode.Dragging => "Selecting a window context",
        WindowBindingMode.PreviewEligible => "Eligible context preview",
        WindowBindingMode.PreviewIneligible => "Cannot bind here",
        WindowBindingMode.Bound => "Window context bound",
        WindowBindingMode.TargetMinimized => "Bound target is minimized",
        WindowBindingMode.Revoked => "Window context revoked",
        WindowBindingMode.Expired => "Window context expired",
        _ => "Unknown window context state",
    };

    public string ReasonCode => snapshot.ReasonCode;

    public bool HasActiveLease => snapshot.HasActiveLease;

    public bool CanAttach => !HasActiveLease && SelectedTarget is not null;

    public bool CanRevoke => HasActiveLease;

    public bool HasVisibleTether =>
        snapshot.Mode is WindowBindingMode.PreviewEligible or WindowBindingMode.Bound;

    public bool IsPreview => snapshot.Mode == WindowBindingMode.PreviewEligible;

    public string TetherLabel => IsPreview
        ? "Eligible context — drop to bind"
        : "Context tether only — no resource authority";

    public string ApplicationName => CurrentTarget?.Identity.ApplicationDisplayName ?? "None";

    public string WindowTitle => CurrentTarget?.Identity.ObservedWindowTitle ?? "Not available";

    public string WindowHandle => snapshot.Lease is null
        ? FormatHandle(CurrentTarget?.Identity.WindowHandle)
        : FormatHandle(snapshot.Lease.Target.WindowHandle);

    public string RootWindowHandle => snapshot.Lease is null
        ? FormatHandle(CurrentTarget?.Identity.RootWindowHandle)
        : FormatHandle(snapshot.Lease.Target.RootWindowHandle);

    public string ProcessId => (snapshot.Lease?.Target.ProcessId ??
        CurrentTarget?.Identity.ProcessId)?.ToString(CultureInfo.InvariantCulture) ?? "None";

    public string ProcessStartedUtc => FormatTime(
        snapshot.Lease?.Target.ProcessStartedAt ?? CurrentTarget?.Identity.ProcessStartedAt);

    public string IssuedUtc => FormatTime(snapshot.Lease?.IssuedAt);

    public string ExpiresUtc => FormatTime(snapshot.Lease?.ExpiresAt);

    public string RevokedUtc => FormatTime(snapshot.Lease?.RevokedAt);

    public string RevocationReason =>
        snapshot.Lease?.RevocationReason?.ToString() ?? "None";

    public string ContextCapabilities => snapshot.Lease is null
        ? "None"
        : string.Join(
            ", ",
            snapshot.Lease.ContextCapabilities.Order().Select(
                static capability => capability.ToString()));

    public string ExactLeaseJson => snapshot.Lease is null
        ? "No WindowLease exists."
        : Encoding.UTF8.GetString(ContractJson.Serialize(
            WindowLeaseContractMapper.ToContract(snapshot.Lease)));

    public CompanionBodyProjection PetBody => fileApprentice.CurrentBody.State ==
        CompanionBodyState.HomeIdle && snapshot.HasActiveLease
            ? CompanionActivityProjector.Project(
                new CompanionActivityFacts(HasVisibleScope: true))
            : fileApprentice.CurrentBody;

    public string PetAccessibleName =>
        $"{fileApprentice.BodyAccessibleName} Window context: {Headline}. " +
        $"{ApplicationName}. {ReasonCode}.";

    public bool ReducedMotion => reducedMotion;

    public WindowTargetChoice? SelectedTarget
    {
        get => selectedTarget;
        set
        {
            if (SetProperty(ref selectedTarget, value))
            {
                OnPropertyChanged(nameof(CanAttach));
            }
        }
    }

    public bool IsRefreshingTargets
    {
        get => isRefreshingTargets;
        private set => SetProperty(ref isRefreshingTargets, value);
    }

    public string LastActionMessage
    {
        get => lastActionMessage;
        private set => SetProperty(ref lastActionMessage, value);
    }

    public void Apply(WindowBindingSnapshot next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (next.Revision < snapshot.Revision)
        {
            return;
        }

        snapshot = next;
        string[] properties =
        [
            nameof(Snapshot),
            nameof(Mode),
            nameof(ModeCode),
            nameof(Headline),
            nameof(ReasonCode),
            nameof(HasActiveLease),
            nameof(CanAttach),
            nameof(CanRevoke),
            nameof(HasVisibleTether),
            nameof(IsPreview),
            nameof(TetherLabel),
            nameof(ApplicationName),
            nameof(WindowTitle),
            nameof(WindowHandle),
            nameof(RootWindowHandle),
            nameof(ProcessId),
            nameof(ProcessStartedUtc),
            nameof(IssuedUtc),
            nameof(ExpiresUtc),
            nameof(RevokedUtc),
            nameof(RevocationReason),
            nameof(ContextCapabilities),
            nameof(ExactLeaseJson),
            nameof(PetBody),
            nameof(PetAccessibleName),
        ];
        foreach (string property in properties)
        {
            OnPropertyChanged(property);
        }

        LastActionMessage = next.Mode switch
        {
            WindowBindingMode.Bound =>
                $"Bound to {ApplicationName} as context only.",
            WindowBindingMode.Revoked =>
                $"Context revoked: {next.ReasonCode}.",
            WindowBindingMode.Expired => "Context lease expired.",
            WindowBindingMode.PreviewIneligible =>
                $"Cannot bind: {next.ReasonCode}.",
            _ => LastActionMessage,
        };
    }

    public void BeginTargetRefresh() => IsRefreshingTargets = true;

    public void CompleteTargetRefresh(IReadOnlyList<WindowTargetSnapshot> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        EligibleTargets.Clear();
        foreach (WindowTargetSnapshot target in targets)
        {
            EligibleTargets.Add(new WindowTargetChoice(target));
        }

        SelectedTarget = EligibleTargets.FirstOrDefault();
        IsRefreshingTargets = false;
        LastActionMessage = EligibleTargets.Count == 0
            ? "No eligible top-level windows are currently available."
            : $"Found {EligibleTargets.Count.ToString(CultureInfo.InvariantCulture)} " +
                "eligible context targets. Select one, then attach.";
    }

    public void FailTargetRefresh(string reasonCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        IsRefreshingTargets = false;
        LastActionMessage = $"Target refresh failed: {reasonCode}.";
    }

    public void ReportAction(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        LastActionMessage = message;
    }

    private void OnFileApprenticePropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(FileApprenticeViewModel.CurrentBody) or
            nameof(FileApprenticeViewModel.BodyAccessibleName))
        {
            OnPropertyChanged(nameof(PetBody));
            OnPropertyChanged(nameof(PetAccessibleName));
        }
    }

    private WindowTargetSnapshot? CurrentTarget =>
        snapshot.ObservedTarget ?? snapshot.Preview?.Target;

    private static string FormatHandle(ulong? handle) =>
        handle is null ? "None" : $"0x{handle.Value:X}";

    private static string FormatTime(DateTimeOffset? value) =>
        value?.ToString("O", CultureInfo.InvariantCulture) ?? "None";

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

public sealed record WindowTargetChoice
{
    public WindowTargetChoice(WindowTargetSnapshot target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Target = target;
        string title = target.Identity.ObservedWindowTitle is null
            ? string.Empty
            : $" — {target.Identity.ObservedWindowTitle}";
        DisplayLabel = $"{target.Identity.ApplicationDisplayName}{title} " +
            $"(PID {target.Identity.ProcessId.ToString(CultureInfo.InvariantCulture)})";
    }

    public WindowTargetSnapshot Target { get; }

    public string DisplayLabel { get; }
}
