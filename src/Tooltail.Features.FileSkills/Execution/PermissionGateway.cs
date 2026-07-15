using Tooltail.Application.Abstractions;
using Tooltail.Domain.Common;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Paths;

namespace Tooltail.Features.FileSkills.Execution;

public sealed record ExecutionAuthorization(
    PlanId PlanId,
    PlanFingerprint Fingerprint,
    DateTimeOffset AuthorizedUtc,
    PlanApproval ConsumedApproval);

public sealed class PermissionGateway
{
    private readonly IClock clock;

    public PermissionGateway(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.clock = clock;
    }

    public DomainResult<ExecutionAuthorization> Authorize(
        ExecutionPlan plan,
        SkillVersion skillVersion,
        LocalFolderGrant grant,
        PlanApproval approval)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(skillVersion);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(approval);

        DateTimeOffset nowUtc = clock.UtcNow;
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            return Denied("permission.non_utc_time", "Permission checks require an authoritative UTC time.");
        }

        if (!CanonicalExecutionPlan.HasValidFingerprint(plan))
        {
            return Denied("permission.plan_fingerprint_invalid", "The plan fingerprint is not canonical.");
        }

        PathSafetyError? pathError = CanonicalExecutionPlan.ValidatePaths(plan.Definition);
        if (pathError is not null)
        {
            return Denied(pathError.Code, pathError.Message);
        }

        ExecutionPlanDefinition definition = plan.Definition;
        if (nowUtc < definition.CreatedUtc || nowUtc >= definition.ExpiresUtc)
        {
            return Denied("permission.plan_expired", "The execution plan is outside its valid lifetime.");
        }

        if (skillVersion.SkillId != definition.SkillId ||
            skillVersion.Number != definition.SkillVersion ||
            !string.Equals(
                skillVersion.SpecificationHash,
                definition.SkillSpecificationHash.Value,
                StringComparison.Ordinal))
        {
            return Denied("permission.skill_mismatch", "The validated skill version does not match the plan.");
        }

        if (!IsExecutableLifecycle(skillVersion.Lifecycle))
        {
            return Denied("permission.skill_not_executable", "The skill lifecycle does not allow execution.");
        }

        if (grant.Id != definition.GrantId || grant.RootIdentity != definition.RootIdentity)
        {
            return Denied("permission.grant_mismatch", "The resource grant does not match the plan.");
        }

        if (!grant.Capabilities.SetEquals(definition.GrantedCapabilities))
        {
            return Denied("permission.grant_actions_changed", "The grant action set changed after planning.");
        }

        foreach (PlannedFileOperation operation in definition.Operations)
        {
            GrantCapability required = RequiredCapability(operation.Primitive);
            if (!grant.Allows(required, nowUtc))
            {
                return Denied("permission.action_not_granted", "A planned action is not currently granted.");
            }
        }

        DomainResult<PlanApproval> consumed = approval.Consume(plan, nowUtc);
        if (!consumed.IsSuccess)
        {
            return Denied(consumed.Error!.Code, consumed.Error.Message);
        }

        return DomainResult.Success(
            new ExecutionAuthorization(
                definition.Id,
                plan.Fingerprint,
                nowUtc,
                consumed.Value!));
    }

    private static bool IsExecutableLifecycle(SkillLifecycleState state) =>
        state is SkillLifecycleState.Approved or
            SkillLifecycleState.Practiced or
            SkillLifecycleState.Reliable or
            SkillLifecycleState.Delegated;

    private static GrantCapability RequiredCapability(FilePrimitive primitive) =>
        primitive switch
        {
            FilePrimitive.EnsureDirectory => GrantCapability.CreateDirectory,
            FilePrimitive.RenameFile => GrantCapability.Rename,
            FilePrimitive.MoveFile => GrantCapability.MoveWithinRoot,
            FilePrimitive.CopyFile => GrantCapability.CopyWithinRoot,
            _ => throw new ArgumentOutOfRangeException(nameof(primitive)),
        };

    private static DomainResult<ExecutionAuthorization> Denied(string code, string message) =>
        DomainResult.Failure<ExecutionAuthorization>(code, message);
}
