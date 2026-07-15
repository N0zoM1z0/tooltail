using Tooltail.Application.Abstractions;
using Tooltail.Domain.Common;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Paths;

namespace Tooltail.Features.FileSkills.Execution;

public enum ExecutionAuthorizationPurpose
{
    Production,
    Rehearsal,
    Undo,
}

public sealed record ExecutionAuthorization(
    PlanId PlanId,
    PlanFingerprint Fingerprint,
    DateTimeOffset AuthorizedUtc,
    PlanApproval ConsumedApproval,
    ExecutionAuthorizationPurpose Purpose = ExecutionAuthorizationPurpose.Production);

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
        PlanApproval approval) =>
        AuthorizeCore(
            plan,
            skillVersion,
            grant,
            approval,
            ExecutionAuthorizationPurpose.Production);

    public DomainResult<ExecutionAuthorization> AuthorizeRehearsal(
        ExecutionPlan plan,
        SkillVersion skillVersion,
        LocalFolderGrant temporaryGrant,
        PlanApproval rehearsalApproval) =>
        AuthorizeCore(
            plan,
            skillVersion,
            temporaryGrant,
            rehearsalApproval,
            ExecutionAuthorizationPurpose.Rehearsal);

    public DomainResult<ExecutionAuthorization> AuthorizeUndo(
        RecoveryPlan plan,
        SkillVersion skillVersion,
        LocalFolderGrant grant,
        PlanApproval undoApproval)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(skillVersion);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(undoApproval);
        if (undoApproval.Purpose != PlanApprovalPurpose.Undo)
        {
            return Denied(
                "permission.approval_purpose_mismatch",
                "Undo requires an approval bound to the exact recovery purpose.");
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            return Denied(
                "permission.non_utc_time",
                "Permission checks require an authoritative UTC time.");
        }

        if (!CanonicalRecoveryPlan.HasValidFingerprint(plan))
        {
            return Denied(
                "permission.plan_fingerprint_invalid",
                "The recovery plan fingerprint is not canonical.");
        }

        PathSafetyError? pathError = CanonicalRecoveryPlan.ValidatePaths(plan.Definition);
        if (pathError is not null)
        {
            return Denied(pathError.Code, pathError.Message);
        }

        RecoveryPlanDefinition definition = plan.Definition;
        string? authorityFailure = ValidateRecoveryAuthority(
            definition,
            skillVersion,
            grant,
            nowUtc);
        if (authorityFailure is not null)
        {
            return Denied(authorityFailure, "The current authority does not match the recovery plan.");
        }

        DomainResult<PlanApproval> consumed = undoApproval.ConsumeUndo(plan, nowUtc);
        if (!consumed.IsSuccess)
        {
            return Denied(consumed.Error!.Code, consumed.Error.Message);
        }

        return DomainResult.Success(
            new ExecutionAuthorization(
                definition.Id,
                plan.Fingerprint,
                nowUtc,
                consumed.Value!,
                ExecutionAuthorizationPurpose.Undo));
    }

    private DomainResult<ExecutionAuthorization> AuthorizeCore(
        ExecutionPlan plan,
        SkillVersion skillVersion,
        LocalFolderGrant grant,
        PlanApproval approval,
        ExecutionAuthorizationPurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(skillVersion);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(approval);
        if (!Enum.IsDefined(purpose))
        {
            return Denied("permission.purpose_unknown", "The execution authorization purpose is unknown.");
        }

        PlanApprovalPurpose requiredApprovalPurpose = RequiredApprovalPurpose(purpose);
        if (approval.Purpose != requiredApprovalPurpose)
        {
            return Denied(
                "permission.approval_purpose_mismatch",
                "The approval purpose does not match the requested execution purpose.");
        }

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

        if (!IsExecutableLifecycle(skillVersion.Lifecycle, purpose))
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
                consumed.Value!,
                purpose));
    }

    public DomainResult<ExecutionAuthorization> Revalidate(
        ExecutionAuthorization authorization,
        ExecutionPlan plan,
        SkillVersion skillVersion,
        LocalFolderGrant grant)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(skillVersion);
        ArgumentNullException.ThrowIfNull(grant);

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
        PlanApproval consumed = authorization.ConsumedApproval;
        if (!Enum.IsDefined(authorization.Purpose))
        {
            return Denied(
                "permission.purpose_unknown",
                "The execution authorization purpose is unknown.");
        }

        if (authorization.Purpose == ExecutionAuthorizationPurpose.Undo)
        {
            return Denied(
                "permission.purpose_mismatch",
                "A normal execution plan requires production or rehearsal authorization.");
        }

        if (authorization.PlanId != definition.Id ||
            authorization.Fingerprint != plan.Fingerprint ||
            consumed.PlanId != definition.Id ||
            consumed.Fingerprint != plan.Fingerprint)
        {
            return Denied("permission.authorization_plan_mismatch", "The execution authorization does not match the exact plan.");
        }

        if (consumed.State != PlanApprovalState.Consumed || consumed.ConsumedUtc is null)
        {
            return Denied("permission.approval_not_consumed", "Execution requires the exact consumed approval.");
        }

        PlanApprovalPurpose requiredApprovalPurpose =
            RequiredApprovalPurpose(authorization.Purpose);
        if (consumed.Purpose != requiredApprovalPurpose)
        {
            return Denied(
                "permission.approval_purpose_mismatch",
                "The consumed approval purpose does not match the execution authorization.");
        }

        if (authorization.AuthorizedUtc != consumed.ConsumedUtc ||
            nowUtc < authorization.AuthorizedUtc ||
            nowUtc >= consumed.ExpiresUtc ||
            nowUtc < definition.CreatedUtc ||
            nowUtc >= definition.ExpiresUtc)
        {
            return Denied("permission.authorization_expired", "The authorization or plan is outside its valid lifetime.");
        }

        if (skillVersion.SkillId != definition.SkillId ||
            skillVersion.Number != definition.SkillVersion ||
            !string.Equals(
                skillVersion.SpecificationHash,
                definition.SkillSpecificationHash.Value,
                StringComparison.Ordinal))
        {
            return Denied("permission.skill_mismatch", "The current skill version does not match the plan.");
        }

        if (!IsExecutableLifecycle(skillVersion.Lifecycle, authorization.Purpose))
        {
            return Denied("permission.skill_not_executable", "The current skill lifecycle does not allow execution.");
        }

        if (grant.Id != definition.GrantId || grant.RootIdentity != definition.RootIdentity)
        {
            return Denied("permission.grant_mismatch", "The current resource grant does not match the plan.");
        }

        if (!grant.Capabilities.SetEquals(definition.GrantedCapabilities))
        {
            return Denied("permission.grant_actions_changed", "The grant action set changed after planning.");
        }

        foreach (PlannedFileOperation operation in definition.Operations)
        {
            if (!grant.Allows(RequiredCapability(operation.Primitive), nowUtc))
            {
                return Denied("permission.action_not_granted", "A planned action is no longer granted.");
            }
        }

        return DomainResult.Success(authorization);
    }

    public DomainResult<ExecutionAuthorization> RevalidateUndo(
        ExecutionAuthorization authorization,
        RecoveryPlan plan,
        SkillVersion skillVersion,
        LocalFolderGrant grant)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(skillVersion);
        ArgumentNullException.ThrowIfNull(grant);

        DateTimeOffset nowUtc = clock.UtcNow;
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            return Denied(
                "permission.non_utc_time",
                "Permission checks require an authoritative UTC time.");
        }

        if (authorization.Purpose != ExecutionAuthorizationPurpose.Undo ||
            authorization.ConsumedApproval.Purpose != PlanApprovalPurpose.Undo)
        {
            return Denied(
                "permission.approval_purpose_mismatch",
                "The consumed approval is not bound to recovery.");
        }

        if (!CanonicalRecoveryPlan.HasValidFingerprint(plan))
        {
            return Denied(
                "permission.plan_fingerprint_invalid",
                "The recovery plan fingerprint is not canonical.");
        }

        PathSafetyError? pathError = CanonicalRecoveryPlan.ValidatePaths(plan.Definition);
        if (pathError is not null)
        {
            return Denied(pathError.Code, pathError.Message);
        }

        RecoveryPlanDefinition definition = plan.Definition;
        PlanApproval consumed = authorization.ConsumedApproval;
        if (authorization.PlanId != definition.Id ||
            authorization.Fingerprint != plan.Fingerprint ||
            consumed.PlanId != definition.Id ||
            consumed.Fingerprint != plan.Fingerprint)
        {
            return Denied(
                "permission.authorization_plan_mismatch",
                "The recovery authorization does not match the exact plan.");
        }

        if (consumed.State != PlanApprovalState.Consumed ||
            consumed.ConsumedUtc is null ||
            authorization.AuthorizedUtc != consumed.ConsumedUtc ||
            nowUtc < authorization.AuthorizedUtc ||
            nowUtc >= consumed.ExpiresUtc)
        {
            return Denied(
                "permission.authorization_expired",
                "The recovery authorization is not current.");
        }

        string? authorityFailure = ValidateRecoveryAuthority(
            definition,
            skillVersion,
            grant,
            nowUtc);
        return authorityFailure is null
            ? DomainResult.Success(authorization)
            : Denied(
                authorityFailure,
                "The current authority no longer matches the recovery plan.");
    }

    private static string? ValidateRecoveryAuthority(
        RecoveryPlanDefinition definition,
        SkillVersion skillVersion,
        LocalFolderGrant grant,
        DateTimeOffset nowUtc)
    {
        if (nowUtc < definition.CreatedUtc || nowUtc >= definition.ExpiresUtc)
        {
            return "permission.plan_expired";
        }

        if (skillVersion.SkillId != definition.SkillId ||
            skillVersion.Number != definition.SkillVersion ||
            !string.Equals(
                skillVersion.SpecificationHash,
                definition.SkillSpecificationHash.Value,
                StringComparison.Ordinal) ||
            !IsExecutableLifecycle(
                skillVersion.Lifecycle,
                ExecutionAuthorizationPurpose.Undo))
        {
            return "permission.skill_mismatch";
        }

        if (grant.Id != definition.GrantId || grant.RootIdentity != definition.RootIdentity)
        {
            return "permission.grant_mismatch";
        }

        if (!grant.Capabilities.SetEquals(definition.GrantedCapabilities))
        {
            return "permission.grant_actions_changed";
        }

        if (!grant.Allows(GrantCapability.Enumerate, nowUtc) ||
            !grant.Allows(GrantCapability.ReadMetadata, nowUtc) ||
            (definition.Operations.Any(static operation =>
                 operation.ExpectedSource.Kind == VerifiedEntryKind.File) &&
             !grant.Allows(GrantCapability.ReadContentHash, nowUtc)))
        {
            return "permission.verification_not_granted";
        }

        return definition.Operations.Any(operation =>
            !grant.Allows(operation.RequiredCapability, nowUtc))
            ? "permission.action_not_granted"
            : null;
    }

    private static bool IsExecutableLifecycle(
        SkillLifecycleState state,
        ExecutionAuthorizationPurpose purpose) =>
        purpose switch
        {
            ExecutionAuthorizationPurpose.Rehearsal =>
                state is SkillLifecycleState.Draft or
                SkillLifecycleState.Approved or
                SkillLifecycleState.Practiced or
                SkillLifecycleState.Reliable or
                SkillLifecycleState.Delegated,
            ExecutionAuthorizationPurpose.Undo => Enum.IsDefined(state),
            ExecutionAuthorizationPurpose.Production =>
                state is SkillLifecycleState.Approved or
                SkillLifecycleState.Practiced or
                SkillLifecycleState.Reliable or
                SkillLifecycleState.Delegated,
            _ => false,
        };

    private static PlanApprovalPurpose RequiredApprovalPurpose(
        ExecutionAuthorizationPurpose purpose) =>
        purpose switch
        {
            ExecutionAuthorizationPurpose.Production => PlanApprovalPurpose.Production,
            ExecutionAuthorizationPurpose.Rehearsal => PlanApprovalPurpose.Rehearsal,
            ExecutionAuthorizationPurpose.Undo => PlanApprovalPurpose.Undo,
            _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
        };

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
