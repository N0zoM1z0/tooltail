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

public sealed record TeachingEpisode
{
    private TeachingEpisode(
        TeachingEpisodeId id,
        CompanionId companionId,
        GrantId grantId,
        DateTimeOffset startedAt,
        TeachingEpisodeState state,
        DateTimeOffset? stoppedAt,
        string? invalidReasonCode)
    {
        Id = id;
        CompanionId = companionId;
        GrantId = grantId;
        StartedAt = startedAt;
        State = state;
        StoppedAt = stoppedAt;
        InvalidReasonCode = invalidReasonCode;
    }

    public TeachingEpisodeId Id { get; }

    public CompanionId CompanionId { get; }

    public GrantId GrantId { get; }

    public DateTimeOffset StartedAt { get; }

    public TeachingEpisodeState State { get; private init; }

    public DateTimeOffset? StoppedAt { get; private init; }

    public string? InvalidReasonCode { get; private init; }

    public static TeachingEpisode Start(
        TeachingEpisodeId id,
        CompanionId companionId,
        GrantId grantId,
        DateTimeOffset startedAt) =>
        new(id, companionId, grantId, startedAt, TeachingEpisodeState.Started, null, null);

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

    public DomainResult<TeachingEpisode> MarkReconciled() =>
        Transition(
            TeachingEpisodeState.Stopped,
            TeachingEpisodeState.Reconciled,
            "teaching.reconcile_invalid_state");

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
