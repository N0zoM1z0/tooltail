using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Domain.Teaching;
using Tooltail.Domain.Windows;

namespace Tooltail.Domain.Tests;

public sealed class IdentityAndLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StrongIdentifiersRejectEmptyGuid()
    {
        Assert.Throws<ArgumentException>(() => new CompanionId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new GrantId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new PlanId(Guid.Empty));
    }

    [Fact]
    public void AggregateBoundariesRejectDefaultStructIdentifiers()
    {
        Assert.Throws<ArgumentException>(
            () => WindowLease.Issue(
                default,
                new CompanionId(Guid.NewGuid()),
                new WindowTargetIdentity(0x10, 0x10, 42, Now, "Synthetic target"),
                Now,
                Now.AddMinutes(1)));
        Assert.Throws<ArgumentException>(
            () => LocalFolderGrant.Issue(
                default,
                new CompanionId(Guid.NewGuid()),
                new ResourceRootIdentity("synthetic-root-identity"),
                [GrantCapability.Enumerate],
                Now));
        Assert.Throws<ArgumentException>(
            () => TeachingEpisode.Start(
                default,
                new CompanionId(Guid.NewGuid()),
                new GrantId(Guid.NewGuid()),
                Now));
    }

    [Fact]
    public void WindowLeaseRevocationIsExplicitAndTerminal()
    {
        WindowLease lease = WindowLease.Issue(
            new LeaseId(Guid.NewGuid()),
            new CompanionId(Guid.NewGuid()),
            new WindowTargetIdentity(0x10, 0x10, 42, Now.AddMinutes(-5), "Synthetic target"),
            Now,
            Now.AddMinutes(30));

        var revoked = lease.Revoke(Now.AddMinutes(1), WindowLeaseRevocationReason.UserRemovedPet);
        var repeated = revoked.Value!.Revoke(Now.AddMinutes(2), WindowLeaseRevocationReason.UserRevoked);

        Assert.True(revoked.IsSuccess);
        Assert.Equal(WindowLeaseState.Revoked, revoked.Value.State);
        Assert.False(repeated.IsSuccess);
        Assert.Equal("window_lease.not_active", repeated.Error?.Code);
    }

    [Fact]
    public void WindowLeaseRequiresUtcIdentityAndAuthorityTimes()
    {
        DateTimeOffset nonUtc = Now.ToOffset(TimeSpan.FromHours(8));

        Assert.Throws<ArgumentException>(
            () => new WindowTargetIdentity(0x10, 0x10, 42, nonUtc, "Synthetic target"));

        WindowTargetIdentity target = new(
            0x10,
            0x10,
            42,
            Now.AddMinutes(-5),
            "Synthetic target");
        Assert.Throws<ArgumentException>(
            () => WindowLease.Issue(
                new LeaseId(Guid.NewGuid()),
                new CompanionId(Guid.NewGuid()),
                target,
                nonUtc,
                nonUtc.AddMinutes(1)));

        WindowLease lease = WindowLease.Issue(
            new LeaseId(Guid.NewGuid()),
            new CompanionId(Guid.NewGuid()),
            target,
            Now,
            Now.AddMinutes(1));
        var revoked = lease.Revoke(nonUtc, WindowLeaseRevocationReason.UserRevoked);

        Assert.False(lease.IsActiveAt(nonUtc));
        Assert.False(revoked.IsSuccess);
        Assert.Equal("window_lease.revocation_time_not_utc", revoked.Error?.Code);
    }

    [Fact]
    public void WindowLeaseContextCapabilitiesAreClosedUniqueAndNonMutating()
    {
        WindowTargetIdentity target = new(
            0x10,
            0x10,
            42,
            Now.AddMinutes(-5),
            "Synthetic target",
            "Display-only title");
        WindowLease lease = WindowLease.Issue(
            new LeaseId(Guid.NewGuid()),
            new CompanionId(Guid.NewGuid()),
            target,
            Now,
            Now.AddMinutes(30));

        Assert.Equal(
            Enum.GetValues<WindowContextCapability>().Order(),
            lease.ContextCapabilities.Order());
        Assert.Equal("Display-only title", lease.Target.ObservedWindowTitle);
        Assert.Throws<ArgumentException>(
            () => WindowLease.Issue(
                new LeaseId(Guid.NewGuid()),
                new CompanionId(Guid.NewGuid()),
                target,
                Now,
                Now.AddMinutes(30),
                [WindowContextCapability.AnchorBody, WindowContextCapability.AnchorBody]));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WindowLease.Issue(
                new LeaseId(Guid.NewGuid()),
                new CompanionId(Guid.NewGuid()),
                target,
                Now,
                Now.AddMinutes(30),
                [(WindowContextCapability)999]));
    }

    [Fact]
    public void WindowTargetAuthorityIdentityIgnoresDisplayTextButDetectsReuse()
    {
        WindowTargetIdentity original = new(
            0x10,
            0x10,
            42,
            Now.AddMinutes(-5),
            "Original display name",
            "Original title");
        WindowTargetIdentity displayChanged = new(
            0x10,
            0x10,
            42,
            Now.AddMinutes(-5),
            "Changed display name",
            "Changed title");
        WindowTargetIdentity reusedHandle = new(
            0x10,
            0x10,
            43,
            Now.AddMinutes(-1),
            "Replacement process");

        Assert.True(original.HasSameAuthorityIdentityAs(displayChanged));
        Assert.False(original.HasSameAuthorityIdentityAs(reusedHandle));
    }

    [Fact]
    public void LeaseCanExpireOnlyAtOrAfterItsDeadline()
    {
        WindowLease lease = WindowLease.Issue(
            new LeaseId(Guid.NewGuid()),
            new CompanionId(Guid.NewGuid()),
            new WindowTargetIdentity(
                0x10,
                0x10,
                42,
                Now.AddMinutes(-5),
                "Synthetic target"),
            Now,
            Now.AddMinutes(30));

        var early = lease.Revoke(Now.AddMinutes(1), WindowLeaseRevocationReason.Expired);
        var elapsedWrongReason = lease.Revoke(
            Now.AddMinutes(30),
            WindowLeaseRevocationReason.UserRevoked);
        var expired = lease.Revoke(
            Now.AddMinutes(30),
            WindowLeaseRevocationReason.Expired);

        Assert.Equal("window_lease.expiry_before_deadline", early.Error?.Code);
        Assert.Equal("window_lease.expired", elapsedWrongReason.Error?.Code);
        Assert.True(expired.IsSuccess);
        Assert.Equal(WindowLeaseState.Expired, expired.Value!.State);
    }

    [Fact]
    public void ResourceGrantRequiresItsOwnCapabilityAndActiveLifetime()
    {
        LocalFolderGrant grant = LocalFolderGrant.Issue(
            new GrantId(Guid.NewGuid()),
            new CompanionId(Guid.NewGuid()),
            new ResourceRootIdentity("synthetic-root-identity"),
            [GrantCapability.Enumerate, GrantCapability.ReadMetadata],
            Now,
            Now.AddMinutes(10));

        Assert.True(grant.Allows(GrantCapability.Enumerate, Now));
        Assert.False(grant.Allows(GrantCapability.Enumerate, Now.AddTicks(-1)));
        Assert.False(grant.Allows(GrantCapability.MoveWithinRoot, Now));
        Assert.False(grant.Allows(GrantCapability.Enumerate, Now.AddMinutes(11)));
        Assert.False(grant.Allows(GrantCapability.Enumerate, Now.ToOffset(TimeSpan.FromHours(8))));
    }

    [Fact]
    public void ResourceGrantRejectsNonUtcAuthorityTimestamps()
    {
        Assert.Throws<ArgumentException>(
            () => LocalFolderGrant.Issue(
                new GrantId(Guid.NewGuid()),
                new CompanionId(Guid.NewGuid()),
                new ResourceRootIdentity("synthetic-root-identity"),
                [GrantCapability.Enumerate],
                Now.ToOffset(TimeSpan.FromHours(8))));

        Assert.Throws<ArgumentException>(
            () => LocalFolderGrant.Issue(
                new GrantId(Guid.NewGuid()),
                new CompanionId(Guid.NewGuid()),
                new ResourceRootIdentity("synthetic-root-identity"),
                [GrantCapability.Enumerate],
                Now,
                Now.AddMinutes(10).ToOffset(TimeSpan.FromHours(8))));

        LocalFolderGrant grant = LocalFolderGrant.Issue(
            new GrantId(Guid.NewGuid()),
            new CompanionId(Guid.NewGuid()),
            new ResourceRootIdentity("synthetic-root-identity"),
            [GrantCapability.Enumerate],
            Now);

        var revoked = grant.Revoke(
            Now.AddMinutes(1).ToOffset(TimeSpan.FromHours(8)),
            "synthetic-revocation");

        Assert.False(revoked.IsSuccess);
        Assert.Equal("resource_grant.revocation_time_not_utc", revoked.Error?.Code);
    }

    [Fact]
    public void TeachingEpisodeRejectsOutOfOrderTransitions()
    {
        TeachingEpisode episode = TeachingEpisode.Start(
            new TeachingEpisodeId(Guid.NewGuid()),
            new CompanionId(Guid.NewGuid()),
            new GrantId(Guid.NewGuid()),
            Now);

        var prematureStop = episode.Stop(Now.AddMinutes(1));
        var baseline = episode.CaptureBaseline();
        var observation = baseline.Value!.BeginObservation();
        var stopped = observation.Value!.Stop(Now.AddMinutes(1));
        var reconciled = stopped.Value!.Reconcile(TeachingEvidenceState.Complete);

        Assert.False(prematureStop.IsSuccess);
        Assert.True(reconciled.IsSuccess);
        Assert.Equal(TeachingEpisodeState.Reconciled, reconciled.Value!.State);
        Assert.Equal(TeachingEvidenceState.Complete, reconciled.Value.EvidenceState);
    }

    [Theory]
    [InlineData(TeachingEvidenceState.Incomplete, "teaching.evidence_incomplete")]
    [InlineData(TeachingEvidenceState.Ambiguous, "teaching.evidence_ambiguous")]
    [InlineData(TeachingEvidenceState.Unsupported, "teaching.evidence_unsupported")]
    public void UnsafeEvidenceCannotBecomeReconciled(
        TeachingEvidenceState evidenceState,
        string expectedReason)
    {
        TeachingEpisode episode = TeachingEpisode.Start(
            new TeachingEpisodeId(Guid.NewGuid()),
            new CompanionId(Guid.NewGuid()),
            new GrantId(Guid.NewGuid()),
            Now);
        TeachingEpisode stopped = episode
            .CaptureBaseline().Value!
            .BeginObservation().Value!
            .Stop(Now.AddMinutes(1)).Value!;

        var result = stopped.Reconcile(evidenceState);

        Assert.True(result.IsSuccess);
        Assert.Equal(TeachingEpisodeState.Invalid, result.Value!.State);
        Assert.Equal(evidenceState, result.Value.EvidenceState);
        Assert.Equal(expectedReason, result.Value.InvalidReasonCode);
    }

    [Fact]
    public void SkillLifecycleCannotSkipEvidenceStates()
    {
        SkillVersion draft = new(
            new SkillId(Guid.NewGuid()),
            new SkillVersionNumber(1),
            parent: null,
            specificationHash: new string('a', 64),
            compilerVersion: "0.1.0",
            minimumExecutorVersion: "0.1.0",
            SkillLifecycleState.Draft,
            Now);

        var invalid = draft.TransitionTo(SkillLifecycleState.Reliable);
        var approved = draft.TransitionTo(SkillLifecycleState.Approved);

        Assert.False(invalid.IsSuccess);
        Assert.True(approved.IsSuccess);
        Assert.Equal(SkillLifecycleState.Approved, approved.Value!.Lifecycle);
    }
}
