using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Execution;

namespace Tooltail.Features.FileSkills.Tests.Execution;

public sealed class PermissionGatewayTests
{
    [Fact]
    public void ExactPlanSkillGrantAndApprovalProduceSingleUseAuthorization()
    {
        ExecutionPlan plan = ExecutionPlanFixture.Plan();
        PlanApproval approval = ExecutionPlanFixture.Approval(plan);
        PermissionGateway gateway = GatewayAt(ExecutionPlanFixture.Now.AddMinutes(2));

        var first = gateway.Authorize(
            plan,
            ExecutionPlanFixture.Skill(),
            ExecutionPlanFixture.Grant(),
            approval);
        var repeated = GatewayAt(ExecutionPlanFixture.Now.AddMinutes(3)).Authorize(
            plan,
            ExecutionPlanFixture.Skill(),
            ExecutionPlanFixture.Grant(),
            first.Value!.ConsumedApproval);

        Assert.True(first.IsSuccess);
        Assert.Equal(PlanApprovalState.Consumed, first.Value.ConsumedApproval.State);
        Assert.False(repeated.IsSuccess);
        Assert.Equal("approval.not_active", repeated.Error?.Code);
    }

    [Fact]
    public void AnyReplannedDestinationInvalidatesPriorApproval()
    {
        ExecutionPlan original = ExecutionPlanFixture.Plan();
        PlanApproval approval = ExecutionPlanFixture.Approval(original);
        ExecutionPlan changed = ExecutionPlanFixture.Plan(
            ExecutionPlanFixture.Definition(
                operations: ExecutionPlanFixture.Operations(
                    destination: "Archive\\2026\\Changed.txt")));

        var result = GatewayAt(ExecutionPlanFixture.Now.AddMinutes(2)).Authorize(
            changed,
            ExecutionPlanFixture.Skill(),
            ExecutionPlanFixture.Grant(),
            approval);

        Assert.False(result.IsSuccess);
        Assert.Equal("approval.plan_mismatch", result.Error?.Code);
    }

    [Fact]
    public void ForgedStoredFingerprintFailsBeforeApprovalConsumption()
    {
        ExecutionPlan canonical = ExecutionPlanFixture.Plan();
        ExecutionPlan forged = new(
            canonical.Definition,
            new PlanFingerprint(new string('f', 64)));
        PlanApproval approval = ExecutionPlanFixture.Approval(forged);

        var result = GatewayAt(ExecutionPlanFixture.Now.AddMinutes(2)).Authorize(
            forged,
            ExecutionPlanFixture.Skill(),
            ExecutionPlanFixture.Grant(),
            approval);

        Assert.False(result.IsSuccess);
        Assert.Equal("permission.plan_fingerprint_invalid", result.Error?.Code);
    }

    [Fact]
    public void CanonicallyHashedButUnsafeStoredPathStillFailsAuthorization()
    {
        ExecutionPlanDefinition unsafeDefinition = ExecutionPlanFixture.Definition(
            operations: ExecutionPlanFixture.Operations(source: "C:\\outside.txt"));
        ExecutionPlan unsafePlan = new(
            unsafeDefinition,
            CanonicalExecutionPlan.ComputeFingerprint(unsafeDefinition));

        var result = GatewayAt(ExecutionPlanFixture.Now.AddMinutes(2)).Authorize(
            unsafePlan,
            ExecutionPlanFixture.Skill(),
            ExecutionPlanFixture.Grant(),
            ExecutionPlanFixture.Approval(unsafePlan));

        Assert.False(result.IsSuccess);
        Assert.Equal("path.drive_relative", result.Error?.Code);
    }

    [Fact]
    public void DraftOrStaleSkillCannotExecute()
    {
        ExecutionPlan plan = ExecutionPlanFixture.Plan();
        PlanApproval approval = ExecutionPlanFixture.Approval(plan);

        var draft = GatewayAt(ExecutionPlanFixture.Now.AddMinutes(2)).Authorize(
            plan,
            ExecutionPlanFixture.Skill(SkillLifecycleState.Draft),
            ExecutionPlanFixture.Grant(),
            approval);
        var stale = GatewayAt(ExecutionPlanFixture.Now.AddMinutes(2)).Authorize(
            plan,
            ExecutionPlanFixture.Skill(SkillLifecycleState.Stale),
            ExecutionPlanFixture.Grant(),
            approval);

        Assert.Equal("permission.skill_not_executable", draft.Error?.Code);
        Assert.Equal("permission.skill_not_executable", stale.Error?.Code);
    }

    [Fact]
    public void DraftCanAuthorizeOnlyExplicitRehearsalPurpose()
    {
        ExecutionPlan plan = ExecutionPlanFixture.Plan();
        PermissionGateway gateway = GatewayAt(ExecutionPlanFixture.Now.AddMinutes(2));

        var rehearsal = gateway.AuthorizeRehearsal(
            plan,
            ExecutionPlanFixture.Skill(SkillLifecycleState.Draft),
            ExecutionPlanFixture.Grant(),
            ExecutionPlanFixture.RehearsalApproval(plan));
        var stale = gateway.AuthorizeRehearsal(
            plan,
            ExecutionPlanFixture.Skill(SkillLifecycleState.Stale),
            ExecutionPlanFixture.Grant(),
            ExecutionPlanFixture.RehearsalApproval(plan));
        var productionApproval = gateway.AuthorizeRehearsal(
            plan,
            ExecutionPlanFixture.Skill(SkillLifecycleState.Draft),
            ExecutionPlanFixture.Grant(),
            ExecutionPlanFixture.Approval(plan));

        Assert.True(rehearsal.IsSuccess);
        Assert.Equal(ExecutionAuthorizationPurpose.Rehearsal, rehearsal.Value!.Purpose);
        Assert.Equal("permission.skill_not_executable", stale.Error?.Code);
        Assert.Equal(
            "permission.approval_purpose_mismatch",
            productionApproval.Error?.Code);
    }

    [Fact]
    public void UndoRequiresFreshRecoveryApprovalAndCurrentGrantEvenForStaleSkill()
    {
        RecoveryPlan plan = RecoveryPlan();
        LocalFolderGrant grant = RecoveryGrant();
        PermissionGateway gateway = GatewayAt(ExecutionPlanFixture.Now.AddMinutes(2));
        PlanApproval approval = PlanApproval.IssueUndo(
            new ApprovalId(Guid.Parse("77777777-7777-4777-8777-777777777777")),
            plan,
            ExecutionPlanFixture.Now.AddMinutes(1),
            ExecutionPlanFixture.Now.AddMinutes(20));

        var authorized = gateway.AuthorizeUndo(
            plan,
            ExecutionPlanFixture.Skill(SkillLifecycleState.Stale),
            grant,
            approval);
        var current = gateway.RevalidateUndo(
            authorized.Value!,
            plan,
            ExecutionPlanFixture.Skill(SkillLifecycleState.Stale),
            grant);
        LocalFolderGrant revoked = grant
            .Revoke(ExecutionPlanFixture.Now.AddMinutes(2), "user_revoked")
            .Value!;
        var denied = gateway.RevalidateUndo(
            authorized.Value!,
            plan,
            ExecutionPlanFixture.Skill(SkillLifecycleState.Stale),
            revoked);

        Assert.True(authorized.IsSuccess);
        Assert.Equal(ExecutionAuthorizationPurpose.Undo, authorized.Value!.Purpose);
        Assert.True(current.IsSuccess);
        Assert.Equal("permission.verification_not_granted", denied.Error?.Code);
    }

    [Fact]
    public void ProductionApprovalCannotAuthorizeRecovery()
    {
        RecoveryPlan plan = RecoveryPlan();
        PlanApproval productionApproval = ExecutionPlanFixture.Approval(
            ExecutionPlanFixture.Plan());

        var result = GatewayAt(ExecutionPlanFixture.Now.AddMinutes(2)).AuthorizeUndo(
            plan,
            ExecutionPlanFixture.Skill(),
            RecoveryGrant(),
            productionApproval);

        Assert.Equal("permission.approval_purpose_mismatch", result.Error?.Code);
    }

    [Fact]
    public void RevokedGrantStopsAnAlreadyApprovedPlan()
    {
        ExecutionPlan plan = ExecutionPlanFixture.Plan();
        LocalFolderGrant revoked = ExecutionPlanFixture.Grant()
            .Revoke(ExecutionPlanFixture.Now.AddMinutes(1), "user_revoked")
            .Value!;

        var result = GatewayAt(ExecutionPlanFixture.Now.AddMinutes(2)).Authorize(
            plan,
            ExecutionPlanFixture.Skill(),
            revoked,
            ExecutionPlanFixture.Approval(plan));

        Assert.False(result.IsSuccess);
        Assert.Equal("permission.action_not_granted", result.Error?.Code);
    }

    [Fact]
    public void ChangedGrantActionSetInvalidatesPlan()
    {
        ExecutionPlan plan = ExecutionPlanFixture.Plan();
        LocalFolderGrant expanded = ExecutionPlanFixture.Grant(
        [
            .. ExecutionPlanFixture.Capabilities,
            GrantCapability.CopyWithinRoot,
        ]);

        var result = GatewayAt(ExecutionPlanFixture.Now.AddMinutes(2)).Authorize(
            plan,
            ExecutionPlanFixture.Skill(),
            expanded,
            ExecutionPlanFixture.Approval(plan));

        Assert.False(result.IsSuccess);
        Assert.Equal("permission.grant_actions_changed", result.Error?.Code);
    }

    [Fact]
    public void ExpiredApprovalCannotAuthorizeUnexpiredPlan()
    {
        ExecutionPlan plan = ExecutionPlanFixture.Plan();
        PlanApproval shortApproval = PlanApproval.Issue(
            ExecutionPlanFixture.ApprovalId,
            plan,
            ExecutionPlanFixture.Now.AddMinutes(1),
            ExecutionPlanFixture.Now.AddMinutes(2));

        var result = GatewayAt(ExecutionPlanFixture.Now.AddMinutes(3)).Authorize(
            plan,
            ExecutionPlanFixture.Skill(),
            ExecutionPlanFixture.Grant(),
            shortApproval);

        Assert.False(result.IsSuccess);
        Assert.Equal("approval.expired", result.Error?.Code);
    }

    [Fact]
    public void ApprovalDecisionAndExpiryMustStayInsidePlanLifetime()
    {
        ExecutionPlan plan = ExecutionPlanFixture.Plan();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlanApproval.Issue(
                ExecutionPlanFixture.ApprovalId,
                plan,
                ExecutionPlanFixture.Now.AddSeconds(-1),
                ExecutionPlanFixture.Now.AddMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlanApproval.Issue(
                ExecutionPlanFixture.ApprovalId,
                plan,
                ExecutionPlanFixture.Now.AddMinutes(1),
                ExecutionPlanFixture.Now.AddMinutes(31)));
    }

    [Fact]
    public void RevokedApprovalCannotAuthorizeExecution()
    {
        ExecutionPlan plan = ExecutionPlanFixture.Plan();
        PlanApproval revoked = ExecutionPlanFixture.Approval(plan)
            .Revoke(ExecutionPlanFixture.Now.AddMinutes(2), "user_revoked")
            .Value!;

        var result = GatewayAt(ExecutionPlanFixture.Now.AddMinutes(3)).Authorize(
            plan,
            ExecutionPlanFixture.Skill(),
            ExecutionPlanFixture.Grant(),
            revoked);

        Assert.False(result.IsSuccess);
        Assert.Equal("approval.not_active", result.Error?.Code);
    }

    [Fact]
    public void ConsumedAuthorizationCanBeRevalidatedOnlyWhileAllAuthorityRemainsCurrent()
    {
        ExecutionPlan plan = ExecutionPlanFixture.Plan();
        MutableClock clock = new(ExecutionPlanFixture.Now.AddMinutes(2));
        PermissionGateway gateway = new(clock);
        ExecutionAuthorization authorization = gateway.Authorize(
            plan,
            ExecutionPlanFixture.Skill(),
            ExecutionPlanFixture.Grant(),
            ExecutionPlanFixture.Approval(plan)).Value!;

        var current = gateway.Revalidate(
            authorization,
            plan,
            ExecutionPlanFixture.Skill(),
            ExecutionPlanFixture.Grant());
        LocalFolderGrant revoked = ExecutionPlanFixture.Grant()
            .Revoke(clock.UtcNow, "user_revoked")
            .Value!;
        var afterRevocation = gateway.Revalidate(
            authorization,
            plan,
            ExecutionPlanFixture.Skill(),
            revoked);

        Assert.True(current.IsSuccess);
        Assert.False(afterRevocation.IsSuccess);
        Assert.Equal("permission.action_not_granted", afterRevocation.Error?.Code);
    }

    [Fact]
    public void RevalidationRejectsExpiredOrUnconsumedAuthorization()
    {
        ExecutionPlan plan = ExecutionPlanFixture.Plan();
        MutableClock clock = new(ExecutionPlanFixture.Now.AddMinutes(2));
        PermissionGateway gateway = new(clock);
        ExecutionAuthorization consumed = gateway.Authorize(
            plan,
            ExecutionPlanFixture.Skill(),
            ExecutionPlanFixture.Grant(),
            ExecutionPlanFixture.Approval(plan)).Value!;
        ExecutionAuthorization unconsumed = consumed with
        {
            ConsumedApproval = ExecutionPlanFixture.Approval(plan),
        };

        var beforeConsumption = gateway.Revalidate(
            unconsumed,
            plan,
            ExecutionPlanFixture.Skill(),
            ExecutionPlanFixture.Grant());
        clock.UtcNow = ExecutionPlanFixture.Now.AddMinutes(20);
        var expired = gateway.Revalidate(
            consumed,
            plan,
            ExecutionPlanFixture.Skill(),
            ExecutionPlanFixture.Grant());

        Assert.Equal("permission.approval_not_consumed", beforeConsumption.Error?.Code);
        Assert.Equal("permission.authorization_expired", expired.Error?.Code);
    }

    private static PermissionGateway GatewayAt(DateTimeOffset utcNow) =>
        new(new FixedClock(utcNow));

    private static RecoveryPlan RecoveryPlan()
    {
        VerifiedEntryEvidence evidence = new(
            VerifiedEntryKind.File,
            "volume-a",
            "file-id-01",
            128,
            ExecutionPlanFixture.Now.AddMinutes(-10),
            ExecutionPlanFixture.Now.AddMinutes(-5),
            attributes: 0,
            new ContentHash(new string('b', 64)));
        RecoveryPlanDefinition definition = new(
            new PlanId(Guid.Parse("66666666-6666-4666-8666-666666666666")),
            new ExecutionId(Guid.Parse("88888888-8888-4888-8888-888888888888")),
            ExecutionPlanFixture.PlanId,
            new PlanFingerprint(new string('c', 64)),
            ExecutionPlanFixture.SkillId,
            new SkillVersionNumber(3),
            new SkillSpecificationHash(new string('a', 64)),
            ExecutionPlanFixture.GrantId,
            new ResourceRootIdentity("winfs-v1:volume-a:root-a"),
            RecoveryCapabilities,
            ExecutionPlanFixture.Now,
            ExecutionPlanFixture.Now.AddMinutes(30),
            [
                new PlannedRecoveryOperation(
                    1,
                    2,
                    FilePrimitive.MoveFile,
                    RecoveryPrimitive.MoveBack,
                    "Archive\\2026\\Report.txt",
                    "Inbox\\Report.txt",
                    evidence,
                    originalDestinationWasAbsent: true),
            ]);
        return CanonicalRecoveryPlan.Create(definition).Value!;
    }

    private static LocalFolderGrant RecoveryGrant() =>
        ExecutionPlanFixture.Grant(RecoveryCapabilities);

    private static readonly GrantCapability[] RecoveryCapabilities =
    [
        GrantCapability.Enumerate,
        GrantCapability.ReadMetadata,
        GrantCapability.ReadContentHash,
        GrantCapability.MoveWithinRoot,
    ];

    private sealed class FixedClock(DateTimeOffset utcNow) : Tooltail.Application.Abstractions.IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : Tooltail.Application.Abstractions.IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
