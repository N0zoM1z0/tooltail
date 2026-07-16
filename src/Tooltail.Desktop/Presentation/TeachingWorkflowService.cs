using System.Text;
using Tooltail.Application.Abstractions;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Teaching;
using Tooltail.Features.FileSkills.Observation;
using Tooltail.Features.FileSkills.Persistence;
using Tooltail.Features.FileSkills.Reconciliation;
using Tooltail.Features.FileSkills.Snapshots;

namespace Tooltail.Desktop.Presentation;

public sealed record TeachingWorkflowResult(
    bool IsSuccess,
    string ReasonCode,
    TeachingEpisode? Episode,
    SnapshotReconciliation? Reconciliation,
    int ExampleCount);

public sealed class TeachingWorkflowService : IAsyncDisposable
{
    private readonly TeachingObservationService observationService;
    private readonly IFileSkillStateStore stateStore;
    private readonly IClock clock;
    private readonly IIdGenerator idGenerator;
    private ActiveTeaching? active;

    public TeachingWorkflowService(
        TeachingObservationService observationService,
        IFileSkillStateStore stateStore,
        IClock clock,
        IIdGenerator idGenerator)
    {
        ArgumentNullException.ThrowIfNull(observationService);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(idGenerator);
        this.observationService = observationService;
        this.stateStore = stateStore;
        this.clock = clock;
        this.idGenerator = idGenerator;
    }

    public bool IsObserving => active is not null;

    public async Task<TeachingWorkflowResult> StartAsync(
        SafeLabGrantResult lab,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lab);
        if (active is not null)
        {
            return Failure("teaching.already_observing");
        }

        if (!lab.IsSuccess || lab.Grant is null || lab.Root is null)
        {
            return Failure("teaching.grant_missing");
        }

        DateTimeOffset now = clock.UtcNow;
        TeachingEpisode episode = TeachingEpisode.Start(
            new TeachingEpisodeId(idGenerator.NewId()),
            lab.Grant.CompanionId,
            lab.Grant.Id,
            now);
        StateWriteResult started = await StoreEpisodeAsync(
            episode,
            baselineId: null,
            finalId: null,
            summary: null,
            examples: [],
            cancellationToken).ConfigureAwait(false);
        if (!started.IsSuccess)
        {
            return Failure(started.FailureCode!);
        }

        TeachingObservationStartResult observed = await observationService.StartAsync(
            lab.Root,
            lab.Grant,
            cancellationToken).ConfigureAwait(false);
        if (!observed.IsActive)
        {
            TeachingEpisode invalid = episode.Invalidate(observed.ReasonCode, clock.UtcNow).Value!;
            _ = await StoreEpisodeAsync(
                invalid,
                baselineId: null,
                finalId: null,
                summary: null,
                examples: [],
                cancellationToken).ConfigureAwait(false);
            return Failure(observed.ReasonCode, invalid);
        }

        Guid baselineId = idGenerator.NewId();
        StateWriteResult snapshotStored = await StoreSnapshotAsync(
            baselineId,
            lab.Grant.Id,
            observed.Baseline!,
            cancellationToken).ConfigureAwait(false);
        if (!snapshotStored.IsSuccess)
        {
            await observed.Session!.DisposeAsync().ConfigureAwait(false);
            TeachingEpisode invalid = episode.Invalidate(
                snapshotStored.FailureCode!,
                clock.UtcNow).Value!;
            _ = await StoreEpisodeAsync(
                invalid,
                null,
                null,
                null,
                [],
                cancellationToken).ConfigureAwait(false);
            return Failure(snapshotStored.FailureCode!, invalid);
        }

        episode = episode.CaptureBaseline().Value!;
        StateWriteResult baselineTransition = await StoreEpisodeAsync(
            episode,
            baselineId,
            null,
            null,
            [],
            cancellationToken).ConfigureAwait(false);
        if (!baselineTransition.IsSuccess)
        {
            await observed.Session!.DisposeAsync().ConfigureAwait(false);
            TeachingEpisode invalid = await TryInvalidateAsync(
                episode,
                baselineTransition.FailureCode!,
                baselineId,
                finalId: null,
                cancellationToken).ConfigureAwait(false);
            return Failure(baselineTransition.FailureCode!, invalid);
        }

        episode = episode.BeginObservation().Value!;
        StateWriteResult observing = await StoreEpisodeAsync(
            episode,
            baselineId,
            null,
            null,
            [],
            cancellationToken).ConfigureAwait(false);
        if (!observing.IsSuccess)
        {
            await observed.Session!.DisposeAsync().ConfigureAwait(false);
            TeachingEpisode invalid = await TryInvalidateAsync(
                episode,
                observing.FailureCode!,
                baselineId,
                finalId: null,
                cancellationToken).ConfigureAwait(false);
            return Failure(observing.FailureCode!, invalid);
        }

        active = new ActiveTeaching(
            lab,
            episode,
            baselineId,
            observed.Session!);
        return new TeachingWorkflowResult(
            true,
            "teaching.observation_active",
            episode,
            null,
            0);
    }

    public async Task<TeachingWorkflowResult> StopAsync(
        CancellationToken cancellationToken = default)
    {
        ActiveTeaching? current = active;
        if (current is null)
        {
            return Failure("teaching.not_observing");
        }

        active = null;
        TeachingObservationResult observed = await current.Session.StopAsync(
            current.Lab.Grant!,
            cancellationToken).ConfigureAwait(false);
        Guid finalId = idGenerator.NewId();
        StateWriteResult finalStored = await StoreSnapshotAsync(
            finalId,
            current.Lab.Grant!.Id,
            observed.Final,
            cancellationToken).ConfigureAwait(false);
        if (!finalStored.IsSuccess)
        {
            TeachingEpisode invalid = await TryInvalidateAsync(
                current.Episode,
                finalStored.FailureCode!,
                current.BaselineId,
                finalId: null,
                cancellationToken).ConfigureAwait(false);
            return Failure(finalStored.FailureCode!, invalid);
        }

        TeachingEpisode episode = current.Episode.Stop(observed.Final.CompletedUtc).Value!;
        StateWriteResult stopped = await StoreEpisodeAsync(
            episode,
            current.BaselineId,
            finalId,
            null,
            [],
            cancellationToken).ConfigureAwait(false);
        if (!stopped.IsSuccess)
        {
            TeachingEpisode invalid = await TryInvalidateAsync(
                episode,
                stopped.FailureCode!,
                current.BaselineId,
                finalId,
                cancellationToken).ConfigureAwait(false);
            return Failure(stopped.FailureCode!, invalid);
        }

        TeachingEvidenceState evidence = EvidenceState(observed.Reconciliation.Status);
        episode = episode.Reconcile(evidence).Value!;
        DemonstrationExampleStateRecord[] examples = Examples(observed.Reconciliation);
        string summary = Encoding.UTF8.GetString(
            FileSkillEvidenceCodec.EncodeReconciliationSummary(observed.Reconciliation));
        StateWriteResult reconciled = await StoreEpisodeAsync(
            episode,
            current.BaselineId,
            finalId,
            summary,
            examples,
            cancellationToken).ConfigureAwait(false);
        return reconciled.IsSuccess
            ? new TeachingWorkflowResult(
                observed.Reconciliation.IsCompilable,
                observed.Reconciliation.ReasonCode,
                episode,
                observed.Reconciliation,
                examples.Length)
            : Failure(reconciled.FailureCode!, episode);
    }

    public async ValueTask DisposeAsync()
    {
        ActiveTeaching? current = active;
        active = null;
        if (current is not null)
        {
            await current.Session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<StateWriteResult> StoreSnapshotAsync(
        Guid id,
        GrantId grantId,
        FolderSnapshot snapshot,
        CancellationToken cancellationToken) =>
        await stateStore.StoreFolderSnapshotAsync(
            new FolderSnapshotStateRecord(
                id,
                grantId,
                snapshot.RootIdentity,
                snapshot.StartedUtc,
                snapshot.CompletedUtc,
                snapshot.IsComplete
                    ? PersistedSnapshotStatus.Complete
                    : PersistedSnapshotStatus.Incomplete,
                snapshot.ReasonCode,
                snapshot.HashedBytes,
                Encoding.UTF8.GetString(FileSkillEvidenceCodec.EncodeSnapshot(snapshot)),
                snapshot.CompletedUtc.AddDays(1)),
            cancellationToken).ConfigureAwait(false);

    private async Task<StateWriteResult> StoreEpisodeAsync(
        TeachingEpisode episode,
        Guid? baselineId,
        Guid? finalId,
        string? summary,
        IReadOnlyList<DemonstrationExampleStateRecord> examples,
        CancellationToken cancellationToken) =>
        await stateStore.StoreTeachingEpisodeAsync(
            new TeachingEpisodeStateRecord(
                episode,
                baselineId,
                finalId,
                summary,
                episode.StartedAt.AddDays(1),
                examples),
            cancellationToken).ConfigureAwait(false);

    private async Task<TeachingEpisode> TryInvalidateAsync(
        TeachingEpisode episode,
        string reasonCode,
        Guid? baselineId,
        Guid? finalId,
        CancellationToken cancellationToken)
    {
        Tooltail.Domain.Common.DomainResult<TeachingEpisode> invalidated =
            episode.Invalidate(reasonCode, clock.UtcNow);
        if (!invalidated.IsSuccess)
        {
            return episode;
        }

        TeachingEpisode invalid = invalidated.Value!;
        _ = await StoreEpisodeAsync(
            invalid,
            baselineId,
            finalId,
            summary: null,
            examples: [],
            cancellationToken).ConfigureAwait(false);
        return invalid;
    }

    private DemonstrationExampleStateRecord[] Examples(
        SnapshotReconciliation reconciliation) =>
        reconciliation.Effects
            .Where(static effect => effect.Kind is
                ReconciledEffectKind.Renamed or
                ReconciledEffectKind.Moved or
                ReconciledEffectKind.Copied)
            .Select(effect => new DemonstrationExampleStateRecord(
                new ExampleId(idGenerator.NewId()),
                effect.Kind switch
                {
                    ReconciledEffectKind.Renamed => FilePrimitive.RenameFile,
                    ReconciledEffectKind.Moved => FilePrimitive.MoveFile,
                    ReconciledEffectKind.Copied => FilePrimitive.CopyFile,
                    _ => throw new ArgumentOutOfRangeException(nameof(reconciliation)),
                },
                effect.SourceRelativePath,
                effect.DestinationRelativePath!,
                effect.Before?.ContentHash is null
                    ? "{}"
                    : $"{{\"contentSha256\":\"{effect.Before.ContentHash.Value}\"}}",
                UserLabel: null))
            .ToArray();

    private static TeachingEvidenceState EvidenceState(
        SnapshotReconciliationStatus status) => status switch
        {
            SnapshotReconciliationStatus.Complete => TeachingEvidenceState.Complete,
            SnapshotReconciliationStatus.Ambiguous or SnapshotReconciliationStatus.Concurrent =>
                TeachingEvidenceState.Ambiguous,
            SnapshotReconciliationStatus.Unsupported => TeachingEvidenceState.Unsupported,
            _ => TeachingEvidenceState.Incomplete,
        };

    private static TeachingWorkflowResult Failure(
        string reasonCode,
        TeachingEpisode? episode = null) =>
        new(false, reasonCode, episode, null, 0);

    private sealed record ActiveTeaching(
        SafeLabGrantResult Lab,
        TeachingEpisode Episode,
        Guid BaselineId,
        TeachingObservationSession Session);
}
