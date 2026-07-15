using Tooltail.Domain.Common;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Domain.Teaching;

public enum TeachingEpisodeState
{
    Started,
    BaselineCaptured,
    ObservingEffects,
    Stopped,
    Reconciled,
    Invalid,
}

public enum TeachingEvidenceState
{
    Pending,
    Complete,
    Incomplete,
    Ambiguous,
    Unsupported,
}

public sealed record TeachingEpisode
{
    private TeachingEpisode(
        TeachingEpisodeId id,
        CompanionId companionId,
        GrantId grantId,
        DateTimeOffset startedAt,
        TeachingEpisodeState state,
        TeachingEvidenceState evidenceState,
        DateTimeOffset? stoppedAt,
        string? invalidReasonCode)
    {
        Id = id;
        CompanionId = companionId;
        GrantId = grantId;
        StartedAt = startedAt;
        State = state;
        EvidenceState = evidenceState;
        StoppedAt = stoppedAt;
        InvalidReasonCode = invalidReasonCode;
    }

    public TeachingEpisodeId Id { get; }

    public CompanionId CompanionId { get; }

    public GrantId GrantId { get; }

    public DateTimeOffset StartedAt { get; }

    public TeachingEpisodeState State { get; private init; }

    public TeachingEvidenceState EvidenceState { get; private init; }

    public DateTimeOffset? StoppedAt { get; private init; }

    public string? InvalidReasonCode { get; private init; }

    public static TeachingEpisode Start(
        TeachingEpisodeId id,
        CompanionId companionId,
        GrantId grantId,
        DateTimeOffset startedAt)
    {
        IdentifierGuard.NotEmpty(id.Value);
        IdentifierGuard.NotEmpty(companionId.Value);
        IdentifierGuard.NotEmpty(grantId.Value);
        return new(
            id,
            companionId,
            grantId,
            startedAt,
            TeachingEpisodeState.Started,
            TeachingEvidenceState.Pending,
            null,
            null);
    }

    public DomainResult<TeachingEpisode> CaptureBaseline() =>
        Transition(
            TeachingEpisodeState.Started,
            TeachingEpisodeState.BaselineCaptured,
            "teaching.baseline_invalid_state");

    public DomainResult<TeachingEpisode> BeginObservation() =>
        Transition(
            TeachingEpisodeState.BaselineCaptured,
            TeachingEpisodeState.ObservingEffects,
            "teaching.observation_invalid_state");

    public DomainResult<TeachingEpisode> Stop(DateTimeOffset stoppedAt)
    {
        if (stoppedAt < StartedAt)
        {
            return DomainResult.Failure<TeachingEpisode>(
                "teaching.stop_before_start",
                "A teaching episode cannot stop before it starts.");
        }

        DomainResult<TeachingEpisode> transition = Transition(
            TeachingEpisodeState.ObservingEffects,
            TeachingEpisodeState.Stopped,
            "teaching.stop_invalid_state");
        return transition.IsSuccess
            ? DomainResult.Success(transition.Value! with { StoppedAt = stoppedAt })
            : transition;
    }

    public DomainResult<TeachingEpisode> Reconcile(TeachingEvidenceState evidenceState)
    {
        if (!Enum.IsDefined(evidenceState) || evidenceState == TeachingEvidenceState.Pending)
        {
            return DomainResult.Failure<TeachingEpisode>(
                "teaching.evidence_state_invalid",
                "Reconciliation requires a terminal normalized evidence state.");
        }

        if (State != TeachingEpisodeState.Stopped)
        {
            return DomainResult.Failure<TeachingEpisode>(
                "teaching.reconcile_invalid_state",
                "Only a stopped teaching episode can reconcile evidence.");
        }

        if (evidenceState == TeachingEvidenceState.Complete)
        {
            return DomainResult.Success(
                this with
                {
                    State = TeachingEpisodeState.Reconciled,
                    EvidenceState = evidenceState,
                });
        }

        string reasonCode = evidenceState switch
        {
            TeachingEvidenceState.Incomplete => "teaching.evidence_incomplete",
            TeachingEvidenceState.Ambiguous => "teaching.evidence_ambiguous",
            TeachingEvidenceState.Unsupported => "teaching.evidence_unsupported",
            _ => throw new ArgumentOutOfRangeException(nameof(evidenceState)),
        };
        return DomainResult.Success(
            this with
            {
                State = TeachingEpisodeState.Invalid,
                EvidenceState = evidenceState,
                InvalidReasonCode = reasonCode,
            });
    }

    public DomainResult<TeachingEpisode> Invalidate(string reasonCode, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        if (State is TeachingEpisodeState.Reconciled or TeachingEpisodeState.Invalid)
        {
            return DomainResult.Failure<TeachingEpisode>(
                "teaching.terminal_state",
                "A terminal teaching episode cannot transition again.");
        }

        if (at < StartedAt)
        {
            return DomainResult.Failure<TeachingEpisode>(
                "teaching.invalidation_before_start",
                "A teaching episode cannot be invalidated before it starts.");
        }

        return DomainResult.Success(
            this with
            {
                State = TeachingEpisodeState.Invalid,
                StoppedAt = at,
                InvalidReasonCode = reasonCode,
            });
    }

    private DomainResult<TeachingEpisode> Transition(
        TeachingEpisodeState required,
        TeachingEpisodeState next,
        string errorCode) =>
        State == required
            ? DomainResult.Success(this with { State = next })
            : DomainResult.Failure<TeachingEpisode>(
                errorCode,
                $"Teaching state '{State}' cannot transition to '{next}'.");
}
