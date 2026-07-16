using Tooltail.Domain.Windows;

namespace Tooltail.Application.Windows;

public enum WindowBindingMode
{
    Home,
    Dragging,
    PreviewEligible,
    PreviewIneligible,
    Bound,
    TargetMinimized,
    Revoked,
    Expired,
}

public sealed record WindowBindingSnapshot(
    WindowBindingMode Mode,
    WindowDiscoveryResult? Preview,
    WindowLease? Lease,
    WindowTargetSnapshot? ObservedTarget,
    string ReasonCode,
    long Revision)
{
    public static WindowBindingSnapshot AtHome { get; } = new(
        WindowBindingMode.Home,
        null,
        null,
        null,
        "window_binding.home",
        Revision: 0);

    public bool HasActiveLease => Lease?.State == WindowLeaseState.Active;
}

public readonly record struct WindowBindingActionResult(
    bool IsSuccess,
    WindowBindingSnapshot Snapshot,
    string ReasonCode)
{
    public static WindowBindingActionResult Success(WindowBindingSnapshot snapshot) =>
        new(true, snapshot, snapshot.ReasonCode);

    public static WindowBindingActionResult Failure(
        WindowBindingSnapshot snapshot,
        string reasonCode) =>
        new(false, snapshot, reasonCode);
}

public sealed class WindowBindingChangedEventArgs(WindowBindingSnapshot snapshot) : EventArgs
{
    public WindowBindingSnapshot Snapshot { get; } = snapshot;
}

public sealed record WindowBindingPolicy
{
    public static WindowBindingPolicy Default { get; } = new(
        TimeSpan.FromMinutes(30),
        maximumKeyboardTargets: 32);

    public WindowBindingPolicy(TimeSpan leaseLifetime, int maximumKeyboardTargets)
    {
        if (leaseLifetime <= TimeSpan.Zero || leaseLifetime > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseLifetime));
        }

        if (maximumKeyboardTargets is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumKeyboardTargets));
        }

        LeaseLifetime = leaseLifetime;
        MaximumKeyboardTargets = maximumKeyboardTargets;
    }

    public TimeSpan LeaseLifetime { get; }

    public int MaximumKeyboardTargets { get; }
}
