using Tooltail.Features.FileSkills.Reconciliation;
using Tooltail.Features.FileSkills.Snapshots;

namespace Tooltail.Features.FileSkills.Observation;

public enum TeachingObservationStartStatus
{
    Active,
    GrantInactive,
    BaselineIncomplete,
    Cancelled,
    WatcherUnavailable,
}

public sealed record TeachingObservationStartResult
{
    internal TeachingObservationStartResult(
        TeachingObservationStartStatus status,
        string reasonCode,
        FolderSnapshot? baseline,
        TeachingObservationSession? session)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        if ((status == TeachingObservationStartStatus.Active) != (session is not null) ||
            (session is not null && baseline is null))
        {
            throw new ArgumentException("Observation start status and session must agree.");
        }

        Status = status;
        ReasonCode = reasonCode;
        Baseline = baseline;
        Session = session;
    }

    public TeachingObservationStartStatus Status { get; }

    public string ReasonCode { get; }

    public FolderSnapshot? Baseline { get; }

    public TeachingObservationSession? Session { get; }

    public bool IsActive => Status == TeachingObservationStartStatus.Active;
}

public sealed record TeachingObservationResult
{
    internal TeachingObservationResult(
        FolderSnapshot baseline,
        FolderSnapshot final,
        WatcherHintBatch watcherHints,
        SnapshotReconciliation reconciliation)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(final);
        ArgumentNullException.ThrowIfNull(watcherHints);
        ArgumentNullException.ThrowIfNull(reconciliation);
        Baseline = baseline;
        Final = final;
        WatcherHints = watcherHints;
        Reconciliation = reconciliation;
    }

    public FolderSnapshot Baseline { get; }

    public FolderSnapshot Final { get; }

    public WatcherHintBatch WatcherHints { get; }

    public SnapshotReconciliation Reconciliation { get; }
}
