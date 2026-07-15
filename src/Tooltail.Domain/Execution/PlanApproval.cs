using Tooltail.Domain.Common;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Domain.Execution;

public enum PlanApprovalState
{
    Active,
    Consumed,
    Revoked,
}

public enum PlanApprovalPurpose
{
    Production,
    Rehearsal,
    Undo,
}

public sealed record PlanApproval
{
    private PlanApproval(
        ApprovalId id,
        PlanId planId,
        PlanFingerprint fingerprint,
        DateTimeOffset approvedUtc,
        DateTimeOffset expiresUtc,
        PlanApprovalPurpose purpose,
        PlanApprovalState state,
        DateTimeOffset? consumedUtc,
        DateTimeOffset? revokedUtc,
        string? revocationReason)
    {
        Id = id;
        PlanId = planId;
        Fingerprint = fingerprint;
        ApprovedUtc = approvedUtc;
        ExpiresUtc = expiresUtc;
        Purpose = purpose;
        State = state;
        ConsumedUtc = consumedUtc;
        RevokedUtc = revokedUtc;
        RevocationReason = revocationReason;
    }

    public ApprovalId Id { get; }

    public PlanId PlanId { get; }

    public PlanFingerprint Fingerprint { get; }

    public DateTimeOffset ApprovedUtc { get; }

    public DateTimeOffset ExpiresUtc { get; }

    public PlanApprovalPurpose Purpose { get; }

    public PlanApprovalState State { get; private init; }

    public DateTimeOffset? ConsumedUtc { get; private init; }

    public DateTimeOffset? RevokedUtc { get; private init; }

    public string? RevocationReason { get; private init; }

    public static PlanApproval Issue(
        ApprovalId id,
        ExecutionPlan plan,
        DateTimeOffset approvedUtc,
        DateTimeOffset expiresUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return IssueCore(
            id,
            plan.Definition.Id,
            plan.Fingerprint,
            plan.Definition.CreatedUtc,
            plan.Definition.ExpiresUtc,
            approvedUtc,
            expiresUtc,
            PlanApprovalPurpose.Production);
    }

    public static PlanApproval IssueRehearsal(
        ApprovalId id,
        ExecutionPlan plan,
        DateTimeOffset approvedUtc,
        DateTimeOffset expiresUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return IssueCore(
            id,
            plan.Definition.Id,
            plan.Fingerprint,
            plan.Definition.CreatedUtc,
            plan.Definition.ExpiresUtc,
            approvedUtc,
            expiresUtc,
            PlanApprovalPurpose.Rehearsal);
    }

    public static PlanApproval IssueUndo(
        ApprovalId id,
        RecoveryPlan plan,
        DateTimeOffset approvedUtc,
        DateTimeOffset expiresUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return IssueCore(
            id,
            plan.Definition.Id,
            plan.Fingerprint,
            plan.Definition.CreatedUtc,
            plan.Definition.ExpiresUtc,
            approvedUtc,
            expiresUtc,
            PlanApprovalPurpose.Undo);
    }

    private static PlanApproval IssueCore(
        ApprovalId id,
        PlanId planId,
        PlanFingerprint fingerprint,
        DateTimeOffset planCreatedUtc,
        DateTimeOffset planExpiresUtc,
        DateTimeOffset approvedUtc,
        DateTimeOffset expiresUtc,
        PlanApprovalPurpose purpose)
    {
        IdentifierGuard.NotEmpty(id.Value);
        IdentifierGuard.NotEmpty(planId.Value);
        ArgumentNullException.ThrowIfNull(fingerprint);
        UtcGuard.RequireUtc(planCreatedUtc, nameof(planCreatedUtc));
        UtcGuard.RequireUtc(planExpiresUtc, nameof(planExpiresUtc));
        UtcGuard.RequireUtc(approvedUtc, nameof(approvedUtc));
        UtcGuard.RequireUtc(expiresUtc, nameof(expiresUtc));
        if (!Enum.IsDefined(purpose))
        {
            throw new ArgumentOutOfRangeException(nameof(purpose));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(approvedUtc, planCreatedUtc);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(approvedUtc, planExpiresUtc);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiresUtc, approvedUtc);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(expiresUtc, planExpiresUtc);

        return new PlanApproval(
            id,
            planId,
            fingerprint,
            approvedUtc,
            expiresUtc,
            purpose,
            PlanApprovalState.Active,
            null,
            null,
            null);
    }

    public DomainResult<PlanApproval> Consume(ExecutionPlan plan, DateTimeOffset consumedUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return ConsumeCore(
            plan.Definition.Id,
            plan.Fingerprint,
            plan.Definition.ExpiresUtc,
            consumedUtc);
    }

    public DomainResult<PlanApproval> ConsumeUndo(
        RecoveryPlan plan,
        DateTimeOffset consumedUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return ConsumeCore(
            plan.Definition.Id,
            plan.Fingerprint,
            plan.Definition.ExpiresUtc,
            consumedUtc);
    }

    private DomainResult<PlanApproval> ConsumeCore(
        PlanId planId,
        PlanFingerprint fingerprint,
        DateTimeOffset planExpiresUtc,
        DateTimeOffset consumedUtc)
    {
        UtcGuard.RequireUtc(consumedUtc, nameof(consumedUtc));
        if (State != PlanApprovalState.Active)
        {
            return DomainResult.Failure<PlanApproval>(
                "approval.not_active",
                "Only an active approval can authorize an execution.");
        }

        if (consumedUtc < ApprovedUtc)
        {
            return DomainResult.Failure<PlanApproval>(
                "approval.before_decision",
                "An approval cannot be consumed before its decision time.");
        }

        if (consumedUtc >= ExpiresUtc || consumedUtc >= planExpiresUtc)
        {
            return DomainResult.Failure<PlanApproval>(
                "approval.expired",
                "The approval or its plan has expired.");
        }

        if (PlanId != planId || Fingerprint != fingerprint)
        {
            return DomainResult.Failure<PlanApproval>(
                "approval.plan_mismatch",
                "The approval does not match the exact execution plan.");
        }

        return DomainResult.Success(this with { State = PlanApprovalState.Consumed, ConsumedUtc = consumedUtc });
    }

    public DomainResult<PlanApproval> Revoke(DateTimeOffset revokedUtc, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        UtcGuard.RequireUtc(revokedUtc, nameof(revokedUtc));
        if (State != PlanApprovalState.Active)
        {
            return DomainResult.Failure<PlanApproval>(
                "approval.not_active",
                "Only an active approval can be revoked.");
        }

        if (revokedUtc < ApprovedUtc)
        {
            return DomainResult.Failure<PlanApproval>(
                "approval.revocation_before_decision",
                "An approval cannot be revoked before its decision time.");
        }

        return DomainResult.Success(
            this with
            {
                State = PlanApprovalState.Revoked,
                RevokedUtc = revokedUtc,
                RevocationReason = reason,
            });
    }
}
