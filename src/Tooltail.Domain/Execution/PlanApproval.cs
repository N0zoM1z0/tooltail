using Tooltail.Domain.Common;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Domain.Execution;

public enum PlanApprovalState
{
    Active,
    Consumed,
    Revoked,
}

public sealed record PlanApproval
{
    private PlanApproval(
        ApprovalId id,
        PlanId planId,
        PlanFingerprint fingerprint,
        DateTimeOffset approvedUtc,
        DateTimeOffset expiresUtc,
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
        IdentifierGuard.NotEmpty(id.Value);
        ArgumentNullException.ThrowIfNull(plan);
        UtcGuard.RequireUtc(approvedUtc, nameof(approvedUtc));
        UtcGuard.RequireUtc(expiresUtc, nameof(expiresUtc));
        ArgumentOutOfRangeException.ThrowIfLessThan(approvedUtc, plan.Definition.CreatedUtc);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(approvedUtc, plan.Definition.ExpiresUtc);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiresUtc, approvedUtc);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(expiresUtc, plan.Definition.ExpiresUtc);

        return new PlanApproval(
            id,
            plan.Definition.Id,
            plan.Fingerprint,
            approvedUtc,
            expiresUtc,
            PlanApprovalState.Active,
            null,
            null,
            null);
    }

    public DomainResult<PlanApproval> Consume(ExecutionPlan plan, DateTimeOffset consumedUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
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

        if (consumedUtc >= ExpiresUtc || consumedUtc >= plan.Definition.ExpiresUtc)
        {
            return DomainResult.Failure<PlanApproval>(
                "approval.expired",
                "The approval or its plan has expired.");
        }

        if (PlanId != plan.Definition.Id || Fingerprint != plan.Fingerprint)
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
