using System.Text;
using Tooltail.Application.Abstractions;
using Tooltail.Contracts.Skills;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Domain.Teaching;
using Tooltail.Features.FileSkills.Compilation;
using Tooltail.Features.FileSkills.Execution;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Features.FileSkills.Reconciliation;
using Tooltail.Features.FileSkills.Skills;
using Tooltail.Features.FileSkills.Snapshots;
using Tooltail.Infrastructure.Sqlite;

namespace Tooltail.SkillFixtureCli;

internal sealed class FixtureRuntime
{
    private static readonly GrantCapability[] Capabilities =
    [
        GrantCapability.Enumerate,
        GrantCapability.ReadMetadata,
        GrantCapability.ReadContentHash,
        GrantCapability.CreateDirectory,
        GrantCapability.Rename,
        GrantCapability.MoveWithinRoot,
        GrantCapability.CopyWithinRoot,
    ];

    private FixtureRuntime(
        FixtureWorkspace workspace,
        FixtureClock clock,
        WindowsPathSafetyService pathSafety,
        CanonicalLocalRoot root,
        CanonicalLocalRoot temporaryRoot,
        LocalFolderGrant grant,
        FolderSnapshotService snapshotService,
        SqliteFileSkillStateStore stateStore,
        SqliteExecutionJournalStore journalStore)
    {
        Workspace = workspace;
        Clock = clock;
        PathSafety = pathSafety;
        Root = root;
        TemporaryRoot = temporaryRoot;
        Grant = grant;
        SnapshotService = snapshotService;
        StateStore = stateStore;
        JournalStore = journalStore;
    }

    public FixtureWorkspace Workspace { get; }

    public FixtureClock Clock { get; }

    public WindowsPathSafetyService PathSafety { get; }

    public CanonicalLocalRoot Root { get; }

    public CanonicalLocalRoot TemporaryRoot { get; }

    public LocalFolderGrant Grant { get; }

    public FolderSnapshotService SnapshotService { get; }

    public SqliteFileSkillStateStore StateStore { get; }

    public SqliteExecutionJournalStore JournalStore { get; }

    public static async Task<FixtureValueResult<FixtureRuntime>> CreateAsync(
        FixtureWorkspace workspace,
        int clockMinute,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        FixtureClock clock = new(workspace.AtMinute(clockMinute));
        PortableFixturePathProbe probe = new(workspace);
        WindowsPathSafetyService pathSafety = new(probe);
        PathSafetyResult<CanonicalLocalRoot> root = pathSafety.CaptureRoot(workspace.RootPath);
        PathSafetyResult<CanonicalLocalRoot> temporary = pathSafety.CaptureRoot(
            workspace.TemporaryPath);
        if (!root.IsSuccess || !temporary.IsSuccess)
        {
            return FixtureValueResult<FixtureRuntime>.Failure(
                root.Error?.Code ?? temporary.Error?.Code ?? "fixture.root_capture_failed");
        }

        if (!workspace.IsStateStorageSafe())
        {
            return FixtureValueResult<FixtureRuntime>.Failure(
                "fixture.state_storage_unsafe");
        }

        LocalFolderGrant grant = LocalFolderGrant.Issue(
            new GrantId(workspace.Id("grant")),
            new CompanionId(workspace.Id("companion")),
            root.Value!.Identity,
            Capabilities,
            workspace.AtMinute(0),
            workspace.AtMinute(120));
        TooltailSqliteDatabase database = new(
            new SqliteDatabaseOptions(
                workspace.DatabasePath,
                typeof(FixtureRuntime).Assembly.GetName().Version?.ToString() ?? "0.0.0"),
            clock,
            new FixtureSequenceIdGenerator(workspace.Manifest.WorkspaceId, "sqlite"));
        SqliteDatabaseInitialization initialized = await database.InitializeAsync(
            cancellationToken).ConfigureAwait(false);
        if (!initialized.IsReady)
        {
            return FixtureValueResult<FixtureRuntime>.Failure(initialized.ReasonCode);
        }

        if (!workspace.IsStateStorageSafe())
        {
            return FixtureValueResult<FixtureRuntime>.Failure(
                "fixture.state_storage_unsafe");
        }

        FixtureRuntime runtime = new(
            workspace,
            clock,
            pathSafety,
            root.Value,
            temporary.Value!,
            grant,
            new FolderSnapshotService(probe, clock),
            new SqliteFileSkillStateStore(database),
            new SqliteExecutionJournalStore(database));
        StateWriteResult authority = await runtime.StoreAuthorityAsync(cancellationToken)
            .ConfigureAwait(false);
        return authority.IsSuccess
            ? FixtureValueResult<FixtureRuntime>.Success(runtime)
            : FixtureValueResult<FixtureRuntime>.Failure(authority.FailureCode!);
    }

    public static SkillVersion SkillVersion(
        SkillSpecContract specification,
        SkillLifecycleState lifecycle)
    {
        SkillSpecificationHash hash = CanonicalSkillSpec.ComputeHash(specification);
        SkillVersion draft = new(
            new SkillId(specification.SkillId),
            new SkillVersionNumber(specification.Version),
            parent: null,
            hash.Value,
            specification.Compiler.Version,
            specification.Compatibility.MinimumExecutorVersion,
            SkillLifecycleState.Draft,
            specification.CreatedAt);
        return lifecycle == SkillLifecycleState.Draft
            ? draft
            : draft.TransitionTo(lifecycle).Value!;
    }

    public async Task<StateWriteResult> StoreSkillAsync(
        SkillSpecContract specification,
        SkillLifecycleState lifecycle,
        CancellationToken cancellationToken = default)
    {
        SkillVersion version = SkillVersion(specification, lifecycle);
        return await StateStore.StoreSkillVersionAsync(
            new SkillVersionStateRecord(
                Grant.CompanionId,
                Workspace.Manifest.SkillName,
                specification.CreatedAt,
                version,
                MakeCurrent: true,
                specification.SchemaVersion,
                Encoding.UTF8.GetString(CanonicalSkillSpec.Encode(specification)),
                specification.Compiler.Version,
                lifecycle == SkillLifecycleState.Draft
                    ? null
                    : Workspace.AtMinute(7),
                SemanticDiffJson: null),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<StateWriteResult> StoreSnapshotAsync(
        string phase,
        FolderSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        Guid snapshotId = Workspace.Id($"snapshot:{phase}");
        string json = Encoding.UTF8.GetString(FixtureArtifacts.EncodeSnapshot(snapshot));
        return await StateStore.StoreFolderSnapshotAsync(
            new FolderSnapshotStateRecord(
                snapshotId,
                Grant.Id,
                snapshot.RootIdentity,
                snapshot.StartedUtc,
                snapshot.CompletedUtc,
                snapshot.IsComplete
                    ? PersistedSnapshotStatus.Complete
                    : PersistedSnapshotStatus.Incomplete,
                snapshot.ReasonCode,
                snapshot.HashedBytes,
                json,
                Workspace.AtMinute(120)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<StateWriteResult> StoreEpisodeAsync(
        SnapshotReconciliation reconciliation,
        CancellationToken cancellationToken = default)
    {
        TeachingEpisode episode = TeachingEpisode.Start(
            new TeachingEpisodeId(Workspace.Id("episode")),
            Grant.CompanionId,
            Grant.Id,
            Workspace.AtMinute(0));
        episode = episode.CaptureBaseline().Value!;
        episode = episode.BeginObservation().Value!;
        episode = episode.Stop(Workspace.AtMinute(2)).Value!;
        TeachingEvidenceState evidence = reconciliation.Status switch
        {
            SnapshotReconciliationStatus.Complete => TeachingEvidenceState.Complete,
            SnapshotReconciliationStatus.IncompleteSnapshot or
                SnapshotReconciliationStatus.WatcherOverflow or
                SnapshotReconciliationStatus.WatcherFault or
                SnapshotReconciliationStatus.WatcherNotQuiesced =>
                TeachingEvidenceState.Incomplete,
            SnapshotReconciliationStatus.Ambiguous or
                SnapshotReconciliationStatus.Concurrent => TeachingEvidenceState.Ambiguous,
            _ => TeachingEvidenceState.Unsupported,
        };
        episode = episode.Reconcile(evidence).Value!;
        DemonstrationExampleStateRecord[] examples = reconciliation.Effects
            .Where(static effect => effect.Kind is
                ReconciledEffectKind.Renamed or
                ReconciledEffectKind.Moved or
                ReconciledEffectKind.Copied)
            .Select((effect, index) => new DemonstrationExampleStateRecord(
                new ExampleId(Workspace.Id($"example:{index + 1}")),
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
                    ? null
                    : $"{{\"contentSha256\":\"{effect.Before.ContentHash.Value}\"}}",
                UserLabel: null))
            .ToArray();
        return await StateStore.StoreTeachingEpisodeAsync(
            new TeachingEpisodeStateRecord(
                episode,
                Workspace.Id("snapshot:baseline"),
                Workspace.Id("snapshot:final"),
                Encoding.UTF8.GetString(FixtureArtifacts.EncodeReconciliation(reconciliation)),
                Workspace.AtMinute(120),
                examples),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<StateWriteResult> StoreAuthorityAsync(
        CancellationToken cancellationToken)
    {
        StateWriteResult companion = await StateStore.StoreCompanionAsync(
            new CompanionStateRecord(
                Grant.CompanionId,
                "Tooltail Fixture Companion",
                Workspace.AtMinute(0),
                IdentitySchemaVersion: 1,
                PresentationJson: "{}"),
            cancellationToken).ConfigureAwait(false);
        if (!companion.IsSuccess)
        {
            return companion;
        }

        return await StateStore.StoreLocalFolderGrantAsync(
            new LocalFolderGrantStateRecord(Grant, ProtectedCanonicalRoot: null),
            cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class FixtureAuthoritySource(
    SkillVersion skillVersion,
    LocalFolderGrant grant) : IExecutionAuthoritySource
{
    public ValueTask<ExecutionAuthorityState?> ReadCurrentAsync(
        SkillId skillId,
        SkillVersionNumber requestedVersion,
        GrantId grantId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ExecutionAuthorityState?>(
            skillId == skillVersion.SkillId &&
            requestedVersion == skillVersion.Number &&
            grantId == grant.Id
                ? new ExecutionAuthorityState(skillVersion, grant)
                : null);
    }
}
