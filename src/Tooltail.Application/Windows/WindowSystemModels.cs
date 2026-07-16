using Tooltail.Domain.Windows;

namespace Tooltail.Application.Windows;

public readonly record struct PhysicalScreenPoint(int X, int Y);

public readonly record struct PhysicalScreenRectangle
{
    public PhysicalScreenRectangle(int left, int top, int right, int bottom)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(right, left);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bottom, top);

        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public int Left { get; }

    public int Top { get; }

    public int Right { get; }

    public int Bottom { get; }

    public int Width => checked(Right - Left);

    public int Height => checked(Bottom - Top);

    public bool Contains(PhysicalScreenPoint point) =>
        point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;
}

public enum WindowDiscoveryStatus
{
    Eligible,
    NoTarget,
    Ineligible,
}

public sealed record WindowDiscoveryResult
{
    private WindowDiscoveryResult(
        WindowDiscoveryStatus status,
        WindowTargetSnapshot? target,
        string reasonCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        if ((status == WindowDiscoveryStatus.Eligible) != (target is not null))
        {
            throw new ArgumentException(
                "Only an eligible discovery result can contain a target.",
                nameof(target));
        }

        Status = status;
        Target = target;
        ReasonCode = reasonCode;
    }

    public WindowDiscoveryStatus Status { get; }

    public WindowTargetSnapshot? Target { get; }

    public string ReasonCode { get; }

    public static WindowDiscoveryResult Eligible(WindowTargetSnapshot target) =>
        new(WindowDiscoveryStatus.Eligible, target, "window_target.eligible");

    public static WindowDiscoveryResult NoTarget(string reasonCode = "window_target.none") =>
        new(WindowDiscoveryStatus.NoTarget, null, reasonCode);

    public static WindowDiscoveryResult Ineligible(string reasonCode) =>
        new(WindowDiscoveryStatus.Ineligible, null, reasonCode);
}

public sealed record WindowTargetSnapshot
{
    public WindowTargetSnapshot(
        WindowTargetIdentity identity,
        PhysicalScreenRectangle bounds,
        bool isMinimized,
        bool isForeground)
    {
        ArgumentNullException.ThrowIfNull(identity);
        Identity = identity;
        Bounds = bounds;
        IsMinimized = isMinimized;
        IsForeground = isForeground;
    }

    public WindowTargetIdentity Identity { get; }

    public PhysicalScreenRectangle Bounds { get; }

    public bool IsMinimized { get; }

    public bool IsForeground { get; }
}

public enum WindowTargetObservationStatus
{
    Valid,
    Destroyed,
    IdentityChanged,
    Ineligible,
}

public sealed record WindowTargetObservation
{
    private WindowTargetObservation(
        WindowTargetObservationStatus status,
        WindowTargetSnapshot? target,
        string reasonCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        if ((status == WindowTargetObservationStatus.Valid) != (target is not null))
        {
            throw new ArgumentException(
                "Only a valid observation can contain a target.",
                nameof(target));
        }

        Status = status;
        Target = target;
        ReasonCode = reasonCode;
    }

    public WindowTargetObservationStatus Status { get; }

    public WindowTargetSnapshot? Target { get; }

    public string ReasonCode { get; }

    public static WindowTargetObservation Valid(WindowTargetSnapshot target) =>
        new(WindowTargetObservationStatus.Valid, target, "window_target.valid");

    public static WindowTargetObservation Destroyed(
        string reasonCode = "window_target.destroyed") =>
        new(WindowTargetObservationStatus.Destroyed, null, reasonCode);

    public static WindowTargetObservation IdentityChanged(
        string reasonCode = "window_target.identity_changed") =>
        new(WindowTargetObservationStatus.IdentityChanged, null, reasonCode);

    public static WindowTargetObservation Ineligible(string reasonCode) =>
        new(WindowTargetObservationStatus.Ineligible, null, reasonCode);
}

public enum WindowTargetSignalKind
{
    LocationChanged,
    MinimizeStarted,
    MinimizeEnded,
    Destroyed,
    Cloaked,
    Uncloaked,
    ForegroundChanged,
    ProcessExited,
    ReconcileRequested,
}

public readonly record struct WindowTargetSignal(
    WindowTargetSignalKind Kind,
    ulong WindowHandle);
