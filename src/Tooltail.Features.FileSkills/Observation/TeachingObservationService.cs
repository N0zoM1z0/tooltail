using Tooltail.Application.Abstractions;
using Tooltail.Domain.Permissions;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Features.FileSkills.Reconciliation;
using Tooltail.Features.FileSkills.Snapshots;

namespace Tooltail.Features.FileSkills.Observation;

public sealed class TeachingObservationService
{
    private readonly FolderSnapshotService snapshotService;
    private readonly IWatcherHintSourceFactory watcherFactory;
    private readonly IClock clock;
    private readonly WatcherHintLimits limits;

    public TeachingObservationService(
        FolderSnapshotService snapshotService,
        IWatcherHintSourceFactory watcherFactory,
        IClock clock,
        WatcherHintLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(snapshotService);
        ArgumentNullException.ThrowIfNull(watcherFactory);
        ArgumentNullException.ThrowIfNull(clock);
        this.snapshotService = snapshotService;
        this.watcherFactory = watcherFactory;
        this.clock = clock;
        this.limits = limits ?? WatcherHintLimits.Default;
    }

    public async Task<TeachingObservationStartResult> StartAsync(
        CanonicalLocalRoot root,
        LocalFolderGrant grant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(grant);
        DateTimeOffset now = clock.UtcNow;
        if (!HasObservationAuthority(root, grant, now))
        {
            return Failure(
                TeachingObservationStartStatus.GrantInactive,
                "observation.grant_inactive");
        }

        FolderSnapshot baseline = await snapshotService
            .CaptureAsync(root, grant, cancellationToken)
            .ConfigureAwait(false);
        if (!baseline.IsComplete)
        {
            TeachingObservationStartStatus status =
                baseline.Status == FolderSnapshotStatus.Cancelled
                    ? TeachingObservationStartStatus.Cancelled
                    : TeachingObservationStartStatus.BaselineIncomplete;
            return Failure(status, "observation.baseline_incomplete", baseline);
        }

        now = clock.UtcNow;
        if (cancellationToken.IsCancellationRequested)
        {
            return Failure(
                TeachingObservationStartStatus.Cancelled,
                "observation.cancelled_before_activation",
                baseline);
        }

        if (!HasObservationAuthority(root, grant, now))
        {
            return Failure(
                TeachingObservationStartStatus.GrantInactive,
                "observation.grant_inactive_after_baseline",
                baseline);
        }

        WatcherHintBuffer hintBuffer = new(root.CanonicalPath, limits.MaximumHints);
        IWatcherHintSource? source = null;
        try
        {
            source = watcherFactory.Create(
                root.CanonicalPath,
                hintBuffer.Record,
                limits.InternalBufferSize);
            source.Start();
            TeachingObservationSession session = new(
                root,
                grant,
                baseline,
                snapshotService,
                source,
                hintBuffer,
                clock,
                limits.QuiescenceTimeout);
            source = null;
            return new TeachingObservationStartResult(
                TeachingObservationStartStatus.Active,
                "observation.active",
                baseline,
                session);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            if (source is not null)
            {
                try
                {
                    await source.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception disposalException) when (!IsFatal(disposalException))
                {
                }
            }

            return Failure(
                TeachingObservationStartStatus.WatcherUnavailable,
                "observation.watcher_unavailable",
                baseline);
        }
    }

    private static bool HasObservationAuthority(
        CanonicalLocalRoot root,
        LocalFolderGrant grant,
        DateTimeOffset now) =>
        now.Offset == TimeSpan.Zero &&
        grant.RootIdentity == root.Identity &&
        grant.Allows(GrantCapability.Enumerate, now) &&
        grant.Allows(GrantCapability.ReadMetadata, now);

    private static TeachingObservationStartResult Failure(
        TeachingObservationStartStatus status,
        string reasonCode,
        FolderSnapshot? baseline = null) =>
        new(status, reasonCode, baseline, session: null);

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or
            StackOverflowException or
            AccessViolationException;
}

public sealed class TeachingObservationSession : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly CanonicalLocalRoot root;
    private readonly LocalFolderGrant originalGrant;
    private readonly FolderSnapshotService snapshotService;
    private readonly IWatcherHintSource source;
    private readonly WatcherHintBuffer hintBuffer;
    private readonly IClock clock;
    private readonly TimeSpan quiescenceTimeout;
    private Task<TeachingObservationResult>? stopTask;
    private bool disposedWithoutResult;

    internal TeachingObservationSession(
        CanonicalLocalRoot root,
        LocalFolderGrant originalGrant,
        FolderSnapshot baseline,
        FolderSnapshotService snapshotService,
        IWatcherHintSource source,
        WatcherHintBuffer hintBuffer,
        IClock clock,
        TimeSpan quiescenceTimeout)
    {
        this.root = root;
        this.originalGrant = originalGrant;
        Baseline = baseline;
        this.snapshotService = snapshotService;
        this.source = source;
        this.hintBuffer = hintBuffer;
        this.clock = clock;
        this.quiescenceTimeout = quiescenceTimeout;
    }

    public FolderSnapshot Baseline { get; }

    public bool IsEvidenceInvalidated => hintBuffer.IsInvalidated;

    public int ObservedHintCount => hintBuffer.AcceptedHintCount;

    public Task<TeachingObservationResult> StopAsync(
        LocalFolderGrant currentGrant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentGrant);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposedWithoutResult, this);
            stopTask ??= StopCoreAsync(currentGrant, cancellationToken);
            return stopTask;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task<TeachingObservationResult>? existingStop;
        lock (gate)
        {
            existingStop = stopTask;
            if (existingStop is null)
            {
                disposedWithoutResult = true;
            }
        }

        if (existingStop is not null)
        {
            await existingStop.ConfigureAwait(false);
            return;
        }

        try
        {
            await source
                .StopAndQuiesceAsync(quiescenceTimeout, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                hintBuffer.Record(WatcherSourceSignal.Fault());
            }
            hintBuffer.SealAndDrain(quiesced: false);
        }
    }

    private async Task<TeachingObservationResult> StopCoreAsync(
        LocalFolderGrant currentGrant,
        CancellationToken cancellationToken)
    {
        bool quiesced = false;
        try
        {
            quiesced = await source
                .StopAndQuiesceAsync(quiescenceTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            quiesced = false;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            hintBuffer.Record(WatcherSourceSignal.Fault());
        }
        finally
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }

        WatcherHintBatch hints = hintBuffer.SealAndDrain(quiesced);
        FolderSnapshot final = GrantMatchesOriginal(currentGrant)
            ? await snapshotService
                .CaptureAsync(root, currentGrant, cancellationToken)
                .ConfigureAwait(false)
            : InactiveFinalSnapshot();
        SnapshotReconciliation reconciliation = SnapshotReconciler.Reconcile(
            Baseline,
            final,
            hints);
        return new TeachingObservationResult(Baseline, final, hints, reconciliation);
    }

    private bool GrantMatchesOriginal(LocalFolderGrant currentGrant) =>
        currentGrant.Id == originalGrant.Id &&
        currentGrant.CompanionId == originalGrant.CompanionId &&
        currentGrant.RootIdentity == originalGrant.RootIdentity &&
        currentGrant.IssuedAt == originalGrant.IssuedAt &&
        currentGrant.ExpiresAt == originalGrant.ExpiresAt &&
        currentGrant.Capabilities.SetEquals(originalGrant.Capabilities);

    private FolderSnapshot InactiveFinalSnapshot()
    {
        DateTimeOffset observed = clock.UtcNow;
        if (observed.Offset != TimeSpan.Zero || observed < Baseline.CompletedUtc)
        {
            observed = Baseline.CompletedUtc;
        }

        return new FolderSnapshot(
            root.Identity,
            observed,
            observed,
            FolderSnapshotStatus.GrantInactive,
            "snapshot.grant_inactive",
            hashedBytes: 0,
            entries: []);
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or
            StackOverflowException or
            AccessViolationException;
}
