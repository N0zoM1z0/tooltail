namespace Tooltail.Platform.Windows.Windowing;

public enum WindowEligibilityPurpose
{
    Discovery,
    ExistingLease,
}

public enum WindowEligibilityDisposition
{
    Eligible,
    Skip,
    Block,
}

public sealed record WindowCandidateFacts
{
    public required bool Exists { get; init; }

    public required bool IsTooltailOwned { get; init; }

    public required bool IsVisible { get; init; }

    public required bool IsCloaked { get; init; }

    public required bool IsChildOnly { get; init; }

    public required bool IsTopLevelRoot { get; init; }

    public required bool IsShellSurface { get; init; }

    public required bool IsTransientToolWindow { get; init; }

    public required bool IsInputTransparent { get; init; }

    public required bool IsMinimized { get; init; }

    public required bool IsHung { get; init; }

    public required bool IsHigherIntegrityOrSecure { get; init; }

    public required bool HasStableProcessIdentity { get; init; }

    public required bool HasUsableBounds { get; init; }
}

public readonly record struct WindowEligibilityDecision(
    WindowEligibilityDisposition Disposition,
    string ReasonCode)
{
    public bool IsEligible => Disposition == WindowEligibilityDisposition.Eligible;
}

public static class WindowEligibilityPolicy
{
    public static WindowEligibilityDecision Evaluate(
        WindowCandidateFacts facts,
        WindowEligibilityPurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!Enum.IsDefined(purpose))
        {
            throw new ArgumentOutOfRangeException(nameof(purpose));
        }

        if (!facts.Exists)
        {
            return Skip("window_target.destroyed");
        }

        if (facts.IsTooltailOwned)
        {
            return Skip("window_target.tooltail_owned");
        }

        if (!facts.IsVisible)
        {
            return Skip("window_target.hidden");
        }

        if (facts.IsCloaked)
        {
            return Skip("window_target.cloaked");
        }

        if (facts.IsChildOnly || !facts.IsTopLevelRoot)
        {
            return Skip("window_target.child_only");
        }

        if (facts.IsShellSurface)
        {
            return Skip("window_target.shell_surface");
        }

        if (facts.IsHigherIntegrityOrSecure)
        {
            return Block("window_target.secure_or_elevated");
        }

        if (!facts.HasStableProcessIdentity)
        {
            return Block("window_target.identity_unavailable");
        }

        if (facts.IsTransientToolWindow)
        {
            return Skip("window_target.transient_tool");
        }

        if (facts.IsInputTransparent)
        {
            return Skip("window_target.input_transparent");
        }

        if (facts.IsHung)
        {
            return Skip("window_target.hung");
        }

        if (!facts.HasUsableBounds)
        {
            return Skip("window_target.empty_bounds");
        }

        if (facts.IsMinimized && purpose == WindowEligibilityPurpose.Discovery)
        {
            return Skip("window_target.minimized");
        }

        return new WindowEligibilityDecision(
            WindowEligibilityDisposition.Eligible,
            "window_target.eligible");
    }

    private static WindowEligibilityDecision Skip(string reasonCode) =>
        new(WindowEligibilityDisposition.Skip, reasonCode);

    private static WindowEligibilityDecision Block(string reasonCode) =>
        new(WindowEligibilityDisposition.Block, reasonCode);
}
