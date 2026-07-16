using Tooltail.Platform.Windows.Windowing;

namespace Tooltail.Platform.Windows.Tests.Windowing;

public sealed class WindowEligibilityPolicyTests
{
    [Fact]
    public void FullyVerifiedTopLevelWindowIsEligible()
    {
        WindowEligibilityDecision decision = WindowEligibilityPolicy.Evaluate(
            EligibleFacts(),
            WindowEligibilityPurpose.Discovery);

        Assert.True(decision.IsEligible);
        Assert.Equal("window_target.eligible", decision.ReasonCode);
    }

    [Fact]
    public void TooltailHiddenCloakedChildShellAndTransientSurfacesAreSkipped()
    {
        WindowCandidateFacts[] facts =
        [
            EligibleFacts() with { IsTooltailOwned = true },
            EligibleFacts() with { IsVisible = false },
            EligibleFacts() with { IsCloaked = true },
            EligibleFacts() with { IsChildOnly = true },
            EligibleFacts() with { IsTopLevelRoot = false },
            EligibleFacts() with { IsShellSurface = true },
            EligibleFacts() with { IsTransientToolWindow = true },
            EligibleFacts() with { IsInputTransparent = true },
            EligibleFacts() with { IsHung = true },
            EligibleFacts() with { HasUsableBounds = false },
        ];

        Assert.All(
            facts,
            candidate => Assert.Equal(
                WindowEligibilityDisposition.Skip,
                WindowEligibilityPolicy.Evaluate(
                    candidate,
                    WindowEligibilityPurpose.Discovery).Disposition));
    }

    [Theory]
    [InlineData(true, true, "window_target.secure_or_elevated")]
    [InlineData(false, false, "window_target.identity_unavailable")]
    public void SecureOrUnverifiableTargetsBlockSelectingAWindowBehindThem(
        bool isSecure,
        bool hasIdentity,
        string expectedReason)
    {
        WindowCandidateFacts facts = EligibleFacts() with
        {
            IsHigherIntegrityOrSecure = isSecure,
            HasStableProcessIdentity = hasIdentity,
        };

        WindowEligibilityDecision decision = WindowEligibilityPolicy.Evaluate(
            facts,
            WindowEligibilityPurpose.Discovery);

        Assert.Equal(WindowEligibilityDisposition.Block, decision.Disposition);
        Assert.Equal(expectedReason, decision.ReasonCode);
    }

    [Fact]
    public void MinimizedTargetCannotBeDiscoveredButExistingLeaseCanTrackIt()
    {
        WindowCandidateFacts minimized = EligibleFacts() with { IsMinimized = true };

        WindowEligibilityDecision discovery = WindowEligibilityPolicy.Evaluate(
            minimized,
            WindowEligibilityPurpose.Discovery);
        WindowEligibilityDecision observation = WindowEligibilityPolicy.Evaluate(
            minimized,
            WindowEligibilityPurpose.ExistingLease);

        Assert.Equal(WindowEligibilityDisposition.Skip, discovery.Disposition);
        Assert.True(observation.IsEligible);
    }

    [Fact]
    public void DestroyedTargetAlwaysFailsClosed()
    {
        WindowEligibilityDecision decision = WindowEligibilityPolicy.Evaluate(
            EligibleFacts() with { Exists = false },
            WindowEligibilityPurpose.ExistingLease);

        Assert.Equal(WindowEligibilityDisposition.Skip, decision.Disposition);
        Assert.Equal("window_target.destroyed", decision.ReasonCode);
    }

    private static WindowCandidateFacts EligibleFacts() => new()
    {
        Exists = true,
        IsTooltailOwned = false,
        IsVisible = true,
        IsCloaked = false,
        IsChildOnly = false,
        IsTopLevelRoot = true,
        IsShellSurface = false,
        IsTransientToolWindow = false,
        IsInputTransparent = false,
        IsMinimized = false,
        IsHung = false,
        IsHigherIntegrityOrSecure = false,
        HasStableProcessIdentity = true,
        HasUsableBounds = true,
    };
}
