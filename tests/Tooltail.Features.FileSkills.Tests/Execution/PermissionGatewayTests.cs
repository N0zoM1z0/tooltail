using Tooltail.Domain.Execution;
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

    private static PermissionGateway GatewayAt(DateTimeOffset utcNow) =>
        new(new FixedClock(utcNow));

    private sealed class FixedClock(DateTimeOffset utcNow) : Tooltail.Application.Abstractions.IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
