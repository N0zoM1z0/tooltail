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
        Assert.False(grant.Allows(GrantCapability.MoveWithinRoot, Now));
        Assert.False(grant.Allows(GrantCapability.Enumerate, Now.AddMinutes(11)));
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
        var reconciled = stopped.Value!.MarkReconciled();

        Assert.False(prematureStop.IsSuccess);
        Assert.True(reconciled.IsSuccess);
        Assert.Equal(TeachingEpisodeState.Reconciled, reconciled.Value!.State);
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
