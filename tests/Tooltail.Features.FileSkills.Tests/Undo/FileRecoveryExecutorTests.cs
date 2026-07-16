using System.Security.Cryptography;
using System.Text;
using Tooltail.Application.Abstractions;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Execution;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Features.FileSkills.Snapshots;
using Tooltail.Features.FileSkills.Undo;
using Tooltail.Testing;

namespace Tooltail.Features.FileSkills.Tests.Undo;

public sealed class FileRecoveryExecutorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 16, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task VerifiedExecutionAndUndoRestoreFixtureWithoutTouchingUnrelatedChange()
    {
        using UndoFixture fixture = new();
        await fixture.ExecuteMoveAsync();
        string unrelated = fixture.Write("unrelated-after.txt", "keep me");

        PreparedUndo prepared = await fixture.PrepareUndoAsync();
        UndoExecutionResult result = await fixture.ExecuteUndoAsync(prepared);

        Assert.True(result.IsVerified, result.ReasonCode);
        Assert.Equal(3, result.Receipt!.VerifiedSteps.Count);
        Assert.Empty(result.Receipt.ResidualEffectCodes);
        Assert.Empty(result.ResidualEffectCodes);
        Assert.Equal("first", File.ReadAllText(fixture.PathOf("Inbox", "a.pdf")));
        Assert.Equal("second", File.ReadAllText(fixture.PathOf("Inbox", "b.pdf")));
        Assert.False(Directory.Exists(fixture.PathOf("Archive")));
        Assert.Equal("keep me", File.ReadAllText(unrelated));
        Assert.All(
            fixture.OriginalPlan.Definition.Operations,
            operation => Assert.Equal(
                StepRecoveryStatus.RolledBack,
                result.OriginalJournal.AssessStep(operation.Sequence).Status));
        Assert.All(
            prepared.Plan.Definition.Operations,
            operation => Assert.Equal(
                StepRecoveryStatus.Verified,
                result.RecoveryJournal!.AssessStep(operation.Sequence).Status));
        Assert.Same(result.Receipt, fixture.Store.RecoveryReceipt);
    }

    [Fact]
    public async Task ModifiedCopiedFileCannotBePlannedForRemoval()
    {
        using UndoFixture fixture = new();
        await fixture.ExecuteCopyAsync();
        string copied = fixture.PathOf("Review", "a.pdf");
        File.WriteAllText(copied, "user changed this copy");

        UndoPlanningResult planning = await fixture.PlanUndoAsync();

        Assert.Equal(UndoPlanningStatus.Conflict, planning.Status);
        Assert.Equal("undo.source_changed", planning.ReasonCode);
        Assert.Equal("user changed this copy", File.ReadAllText(copied));
        Assert.Single(fixture.Store.ExecutionIds);
    }

    [Fact]
    public async Task NonEmptyCreatedDirectoryRefusesWholeUndoBeforeMutation()
    {
        using UndoFixture fixture = new();
        await fixture.ExecuteMoveAsync();
        fixture.Write(Path.Combine("Archive", "keep.txt"), "unrelated");

        UndoPlanningResult planning = await fixture.PlanUndoAsync();

        Assert.Equal(UndoPlanningStatus.Conflict, planning.Status);
        Assert.Equal("undo.created_directory_not_empty", planning.ReasonCode);
        Assert.False(File.Exists(fixture.PathOf("Inbox", "a.pdf")));
        Assert.True(File.Exists(fixture.PathOf("Archive", "a.pdf")));
        Assert.True(File.Exists(fixture.PathOf("Archive", "keep.txt")));
    }

    [Fact]
    public async Task OccupiedOriginalPathIsNeverOverwritten()
    {
        using UndoFixture fixture = new();
        await fixture.ExecuteMoveAsync();
        string occupied = fixture.Write(Path.Combine("Inbox", "a.pdf"), "later user file");

        UndoPlanningResult planning = await fixture.PlanUndoAsync();

        Assert.Equal(UndoPlanningStatus.Conflict, planning.Status);
        Assert.Equal("undo.destination_not_absent", planning.ReasonCode);
        Assert.Equal("later user file", File.ReadAllText(occupied));
        Assert.Equal("first", File.ReadAllText(fixture.PathOf("Archive", "a.pdf")));
    }

    [Fact]
    public async Task RevocationAfterIntentStopsBeforeRecoveryPrimitive()
    {
        using UndoFixture fixture = new();
        await fixture.ExecuteMoveAsync();
        PreparedUndo prepared = await fixture.PrepareUndoAsync();
        RecordingRecoveryFaultInjector injector = new(
            context =>
            {
                if (context is
                    {
                        Boundary: FileExecutionBoundary.BeforePrimitive,
                        StepSequence: 1,
                    })
                {
                    fixture.Authority.Revoke();
                }
            });

        UndoExecutionResult result = await fixture.ExecuteUndoAsync(prepared, injector);

        Assert.Equal(UndoExecutionStatus.RecoveryRequired, result.Status);
        Assert.Equal("permission.verification_not_granted", result.ReasonCode);
        Assert.Equal(
            StepRecoveryStatus.RecoveryRequired,
            result.RecoveryJournal!.AssessStep(1).Status);
        Assert.Equal(
            StepRecoveryStatus.Verified,
            result.OriginalJournal.AssessStep(3).Status);
        Assert.True(File.Exists(fixture.PathOf("Archive", "b.pdf")));
        Assert.False(File.Exists(fixture.PathOf("Inbox", "b.pdf")));
        Assert.Contains(
            "undo.recovery_step_1_requires_inspection",
            result.ResidualEffectCodes);
    }

    [Fact]
    public async Task CrashAfterPrimitiveLeavesExactDurablePrefixAndNoRollbackLink()
    {
        using UndoFixture fixture = new();
        await fixture.ExecuteCopyAsync();
        PreparedUndo prepared = await fixture.PrepareUndoAsync();
        RecordingRecoveryFaultInjector injector = new(
            context =>
            {
                if (context is
                    {
                        Boundary: FileExecutionBoundary.AfterPrimitive,
                        StepSequence: 1,
                    })
                {
                    throw new InjectedRecoveryCrashException();
                }
            });

        await Assert.ThrowsAsync<InjectedRecoveryCrashException>(
            () => fixture.ExecuteUndoAsync(prepared, injector));

        Assert.False(File.Exists(fixture.PathOf("Review", "a.pdf")));
        Assert.Equal(
            2,
            fixture.Store.Events(prepared.Request.ExecutionId).Count);
        Assert.IsType<RecoveryStepIntentRecordedEvent>(
            fixture.Store.Events(prepared.Request.ExecutionId)[1]);
        Assert.DoesNotContain(
            fixture.Store.Events(fixture.OriginalExecution.Journal!.ExecutionId),
            static journalEvent => journalEvent is StepRolledBackEvent);
        Assert.Null(fixture.Store.RecoveryReceipt);
    }

    [Theory]
    [InlineData(FileExecutionBoundary.JournalOpened, 1, false, 0, false)]
    [InlineData(FileExecutionBoundary.StepIntentPersisted, 2, false, 0, false)]
    [InlineData(FileExecutionBoundary.BeforePrimitive, 2, false, 0, false)]
    [InlineData(FileExecutionBoundary.AfterPrimitive, 2, true, 0, false)]
    [InlineData(FileExecutionBoundary.MutationObservedPersisted, 3, true, 0, false)]
    [InlineData(FileExecutionBoundary.StepCommittedPersisted, 4, true, 0, false)]
    [InlineData(FileExecutionBoundary.StepVerifiedPersisted, 5, true, 0, false)]
    [InlineData(FileExecutionBoundary.OriginalStepRollbackLinked, 5, true, 1, false)]
    [InlineData(FileExecutionBoundary.ReceiptPersisted, 9, true, 2, true)]
    public async Task CrashAtEveryRecoveryMutationBoundaryPreservesExactProofPrefix(
        FileExecutionBoundary boundary,
        int expectedRecoveryEventCount,
        bool expectedCopyRemoved,
        int expectedRollbackLinks,
        bool expectedReceipt)
    {
        using UndoFixture fixture = new();
        await fixture.ExecuteCopyAsync();
        PreparedUndo prepared = await fixture.PrepareUndoAsync();
        RecordingRecoveryFaultInjector injector = new(
            context =>
            {
                if (context.Boundary == boundary &&
                    (context.StepSequence is null or 1))
                {
                    throw new InjectedRecoveryCrashException();
                }
            });

        await Assert.ThrowsAsync<InjectedRecoveryCrashException>(
            () => fixture.ExecuteUndoAsync(prepared, injector));

        Assert.Equal(
            expectedRecoveryEventCount,
            fixture.Store.Events(prepared.Request.ExecutionId).Count);
        Assert.Equal(
            expectedCopyRemoved,
            !File.Exists(fixture.PathOf("Review", "a.pdf")));
        Assert.Equal(
            expectedRollbackLinks,
            fixture.Store.Events(fixture.OriginalExecution.Journal!.ExecutionId)
                .Count(static journalEvent => journalEvent is StepRolledBackEvent));
        Assert.Equal(expectedReceipt, fixture.Store.RecoveryReceipt is not null);
    }

    [Fact]
    public async Task ManuallyForgedRemovalCannotBypassOriginalPlanAndReceiptProof()
    {
        using UndoFixture fixture = new();
        await fixture.ExecuteCopyAsync();
        PreparedUndo prepared = await fixture.PrepareUndoAsync();
        RecoveryPlanDefinition exact = prepared.Plan.Definition;
        PlannedRecoveryOperation proven = exact.Operations[0];
        PlannedRecoveryOperation forgedRemoval = new(
            proven.Sequence,
            proven.OriginalStepSequence,
            proven.OriginalPrimitive,
            proven.Primitive,
            "victim.txt",
            destinationRelativePath: null,
            proven.ExpectedSource,
            originalDestinationWasAbsent: true);
        RecoveryPlan forgedPlan = CanonicalRecoveryPlan.Create(
            new RecoveryPlanDefinition(
                new PlanId(Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee")),
                exact.OriginalExecutionId,
                exact.OriginalPlanId,
                exact.OriginalPlanFingerprint,
                exact.SkillId,
                exact.SkillVersion,
                exact.SkillSpecificationHash,
                exact.GrantId,
                exact.RootIdentity,
                exact.GrantedCapabilities,
                exact.CreatedUtc,
                exact.ExpiresUtc,
                [forgedRemoval, exact.Operations[1]])).Value!;
        PlanApproval approval = PlanApproval.IssueUndo(
            new ApprovalId(Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff")),
            forgedPlan,
            Now,
            forgedPlan.Definition.ExpiresUtc);
        ExecutionAuthorization authorization = new PermissionGateway(fixture.Clock)
            .AuthorizeUndo(forgedPlan, fixture.SkillVersion, fixture.Grant, approval)
            .Value!;

        Assert.Throws<ArgumentException>(
            () => new UndoExecutionRequest(
                new ExecutionId(Guid.Parse("12121212-1212-4212-8212-121212121212")),
                new ReceiptId(Guid.Parse("13131313-1313-4313-8313-131313131313")),
                forgedPlan,
                authorization,
                fixture.OriginalPlan,
                fixture.OriginalExecution.Journal!,
                fixture.OriginalExecution.Receipt!,
                fixture.Root));
    }

    [Fact]
    public async Task RecoveryProofRequiresEveryInverseInStrictReverseOrder()
    {
        using UndoFixture fixture = new();
        await fixture.ExecuteCopyAsync();
        PreparedUndo prepared = await fixture.PrepareUndoAsync();
        RecoveryPlanDefinition exact = prepared.Plan.Definition;
        RecoveryPlan omitted = RebuildRecoveryPlan(
            exact,
            Guid.Parse("14141414-1414-4414-8414-141414141414"),
            [exact.Operations[0]]);
        RecoveryPlan reordered = RebuildRecoveryPlan(
            exact,
            Guid.Parse("15151515-1515-4515-8515-151515151515"),
            [
                Resequence(exact.Operations[1], 1),
                Resequence(exact.Operations[0], 2),
            ]);

        AssertRecoveryRequestRejected(
            fixture,
            omitted,
            Guid.Parse("16161616-1616-4616-8616-161616161616"),
            Guid.Parse("17171717-1717-4717-8717-171717171717"));
        AssertRecoveryRequestRejected(
            fixture,
            reordered,
            Guid.Parse("18181818-1818-4818-8818-181818181818"),
            Guid.Parse("19191919-1919-4919-8919-191919191919"));
    }

    [Fact]
    public async Task RecoveryProofCannotPredateReceiptOrChangeOriginalSkillBinding()
    {
        using UndoFixture fixture = new();
        await fixture.ExecuteCopyAsync();
        PreparedUndo prepared = await fixture.PrepareUndoAsync();
        RecoveryPlanDefinition exact = prepared.Plan.Definition;
        RecoveryPlan predating = RebuildRecoveryPlan(
            exact,
            Guid.Parse("20202020-2020-4020-8020-202020202020"),
            exact.Operations,
            createdUtc: fixture.OriginalExecution.Receipt!.CompletedUtc.AddTicks(-1));
        SkillVersion changedSkill = new(
            fixture.SkillVersion.SkillId,
            fixture.SkillVersion.Number,
            fixture.SkillVersion.Parent,
            new string('f', 64),
            fixture.SkillVersion.CompilerVersion,
            fixture.SkillVersion.MinimumExecutorVersion,
            fixture.SkillVersion.Lifecycle,
            fixture.SkillVersion.CreatedAt);
        RecoveryPlan rebound = RebuildRecoveryPlan(
            exact,
            Guid.Parse("21212121-2121-4121-8121-212121212121"),
            exact.Operations,
            specificationHash: new SkillSpecificationHash(changedSkill.SpecificationHash));

        AssertRecoveryRequestRejected(
            fixture,
            predating,
            Guid.Parse("22222222-2222-4222-8222-222222222223"),
            Guid.Parse("23232323-2323-4323-8323-232323232323"));
        AssertRecoveryRequestRejected(
            fixture,
            rebound,
            Guid.Parse("24242424-2424-4424-8424-242424242424"),
            Guid.Parse("25252525-2525-4525-8525-252525252525"),
            changedSkill);
    }

    private static RecoveryPlan RebuildRecoveryPlan(
        RecoveryPlanDefinition source,
        Guid planId,
        IEnumerable<PlannedRecoveryOperation> operations,
        DateTimeOffset? createdUtc = null,
        SkillSpecificationHash? specificationHash = null) =>
        CanonicalRecoveryPlan.Create(
            new RecoveryPlanDefinition(
                new PlanId(planId),
                source.OriginalExecutionId,
                source.OriginalPlanId,
                source.OriginalPlanFingerprint,
                source.SkillId,
                source.SkillVersion,
                specificationHash ?? source.SkillSpecificationHash,
                source.GrantId,
                source.RootIdentity,
                source.GrantedCapabilities,
                createdUtc ?? source.CreatedUtc,
                source.ExpiresUtc,
                operations)).Value!;

    private static PlannedRecoveryOperation Resequence(
        PlannedRecoveryOperation operation,
        int sequence) =>
        new(
            sequence,
            operation.OriginalStepSequence,
            operation.OriginalPrimitive,
            operation.Primitive,
            operation.SourceRelativePath,
            operation.DestinationRelativePath,
            operation.ExpectedSource,
            operation.OriginalDestinationWasAbsent);

    private static void AssertRecoveryRequestRejected(
        UndoFixture fixture,
        RecoveryPlan plan,
        Guid approvalId,
        Guid executionId,
        SkillVersion? authorizationSkill = null)
    {
        PlanApproval approval = PlanApproval.IssueUndo(
            new ApprovalId(approvalId),
            plan,
            Now,
            plan.Definition.ExpiresUtc);
        ExecutionAuthorization authorization = new PermissionGateway(fixture.Clock)
            .AuthorizeUndo(
                plan,
                authorizationSkill ?? fixture.SkillVersion,
                fixture.Grant,
                approval)
            .Value!;

        Assert.Throws<ArgumentException>(
            () => new UndoExecutionRequest(
                new ExecutionId(executionId),
                new ReceiptId(Guid.Parse("26262626-2626-4626-8626-262626262626")),
                plan,
                authorization,
                fixture.OriginalPlan,
                fixture.OriginalExecution.Journal!,
                fixture.OriginalExecution.Receipt!,
                fixture.Root));
    }

    private sealed class UndoFixture : IDisposable
    {
        private readonly TemporaryDirectory temporary = new();
        private readonly FixedClock clock = new(Now);
        private readonly PortableRecoveryProbe probe = new();
        private readonly WindowsPathSafetyService pathSafety;
        private readonly FolderSnapshotService snapshotService;
        private readonly LocalFolderGrant grant;
        private readonly SkillVersion skillVersion;

        public UndoFixture()
        {
            Directory.CreateDirectory(PathOf("Inbox"));
            Write(Path.Combine("Inbox", "a.pdf"), "first");
            Write(Path.Combine("Inbox", "b.pdf"), "second");
            pathSafety = new WindowsPathSafetyService(probe);
            Root = CaptureRoot();
            grant = LocalFolderGrant.Issue(
                new GrantId(Guid.Parse("22222222-2222-4222-8222-222222222222")),
                new CompanionId(Guid.Parse("33333333-3333-4333-8333-333333333333")),
                Root.Identity,
                Capabilities,
                Now.AddDays(-1),
                Now.AddDays(8));
            skillVersion = new SkillVersion(
                new SkillId(Guid.Parse("44444444-4444-4444-8444-444444444444")),
                new SkillVersionNumber(1),
                null,
                new string('a', 64),
                "0.1.0",
                "0.1.0",
                SkillLifecycleState.Approved,
                Now.AddDays(-1));
            Authority = new MutableAuthority(skillVersion, grant, Now);
            Store = new MultiExecutionJournalStore();
            snapshotService = new FolderSnapshotService(probe, clock);
        }

        public CanonicalLocalRoot Root { get; }

        public MutableAuthority Authority { get; }

        public IClock Clock => clock;

        public SkillVersion SkillVersion => skillVersion;

        public LocalFolderGrant Grant => grant;

        public MultiExecutionJournalStore Store { get; }

        public ExecutionPlan OriginalPlan { get; private set; } = null!;

        public FileExecutionResult OriginalExecution { get; private set; } = null!;

        public string PathOf(params string[] parts) =>
            parts.Aggregate(temporary.Path, Path.Combine);

        public string Write(string relativePath, string content)
        {
            string path = Path.Combine(temporary.Path, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            File.SetLastWriteTimeUtc(
                path,
                new DateTime(2026, 6, 15, 3, 2, 1, DateTimeKind.Utc));
            return path;
        }

        public Task ExecuteMoveAsync() => ExecuteOriginalAsync(copy: false);

        public Task ExecuteCopyAsync() => ExecuteOriginalAsync(copy: true);

        public async Task<UndoPlanningResult> PlanUndoAsync()
        {
            FolderSnapshot current = await snapshotService.CaptureAsync(Root, grant);
            return new UndoPlanner().Plan(
                new UndoPlanningRequest(
                    new PlanId(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")),
                    OriginalPlan,
                    OriginalExecution.Journal!,
                    OriginalExecution.Receipt!,
                    skillVersion,
                    grant,
                    current,
                    Now,
                    Now.AddMinutes(10)));
        }

        public async Task<PreparedUndo> PrepareUndoAsync()
        {
            UndoPlanningResult planning = await PlanUndoAsync();
            Assert.True(planning.IsReady, planning.ReasonCode);
            RecoveryPlan plan = planning.Plan!;
            PlanApproval approval = PlanApproval.IssueUndo(
                new ApprovalId(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")),
                plan,
                Now,
                plan.Definition.ExpiresUtc);
            ExecutionAuthorization authorization = new PermissionGateway(clock)
                .AuthorizeUndo(plan, skillVersion, grant, approval)
                .Value!;
            UndoExecutionRequest request = new(
                new ExecutionId(Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc")),
                new ReceiptId(Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd")),
                plan,
                authorization,
                OriginalPlan,
                OriginalExecution.Journal!,
                OriginalExecution.Receipt!,
                Root);
            return new PreparedUndo(plan, request);
        }

        public Task<UndoExecutionResult> ExecuteUndoAsync(
            PreparedUndo prepared,
            IRecoveryExecutionFaultInjector? injector = null) =>
            new FileSkillExecutor(
                clock,
                Authority,
                Store,
                pathSafety,
                snapshotService).ExecuteUndoAsync(prepared.Request, injector);

        public void Dispose() => temporary.Dispose();

        private async Task ExecuteOriginalAsync(bool copy)
        {
            FolderSnapshot snapshot = await snapshotService.CaptureAsync(Root, grant);
            Assert.True(snapshot.IsComplete, snapshot.ReasonCode);
            FolderSnapshotEntry first = Entry(snapshot, "Inbox\\a.pdf");
            FolderSnapshotEntry second = Entry(snapshot, "Inbox\\b.pdf");
            string destinationDirectory = copy ? "Review" : "Archive";
            List<PlannedFileOperation> operations =
            [
                new PlannedFileOperation(
                    1,
                    FilePrimitive.EnsureDirectory,
                    sourceRelativePath: null,
                    destinationDirectory,
                    sourceFingerprint: null,
                    DestinationPrecondition.Absent,
                    ExpectedSourceState.NotApplicable,
                    ExpectedDestinationState.DirectoryPresent),
                Operation(2, first, destinationDirectory, copy),
            ];
            if (!copy)
            {
                operations.Add(Operation(3, second, destinationDirectory, copy: false));
            }

            ExecutionPlanDefinition definition = new(
                new PlanId(Guid.Parse("55555555-5555-4555-8555-555555555555")),
                skillVersion.SkillId,
                skillVersion.Number,
                new SkillSpecificationHash(skillVersion.SpecificationHash),
                grant.Id,
                Root.Identity,
                grant.Capabilities,
                Now.AddMinutes(-1),
                Now.AddHours(1),
                operations);
            OriginalPlan = CanonicalExecutionPlan.Create(definition).Value!;
            PlanApproval approval = PlanApproval.Issue(
                new ApprovalId(Guid.Parse("66666666-6666-4666-8666-666666666666")),
                OriginalPlan,
                Now,
                Now.AddMinutes(30));
            ExecutionAuthorization authorization = new PermissionGateway(clock)
                .Authorize(OriginalPlan, skillVersion, grant, approval)
                .Value!;
            FileSkillExecutor executor = new(
                clock,
                Authority,
                Store,
                pathSafety,
                snapshotService);
            OriginalExecution = await executor.ExecuteAsync(
                new FileExecutionRequest(
                    new ExecutionId(Guid.Parse("77777777-7777-4777-8777-777777777777")),
                    new ReceiptId(Guid.Parse("88888888-8888-4888-8888-888888888888")),
                    OriginalPlan,
                    authorization,
                    Root,
                    FileExecutionMode.Production,
                    TimeSpan.FromDays(7)));
            Assert.True(OriginalExecution.IsVerified, OriginalExecution.ReasonCode);
        }

        private CanonicalLocalRoot CaptureRoot()
        {
            FileSystemPathProbeResult root = probe.Probe(temporary.Path);
            return new CanonicalLocalRoot(
                Path.GetFullPath(temporary.Path),
                new ResourceRootIdentity(
                    $"winfs-v1:{root.VolumeIdentity}:{root.EntryIdentity}"),
                root.VolumeIdentity!,
                root.EntryIdentity!);
        }

        private static PlannedFileOperation Operation(
            int sequence,
            FolderSnapshotEntry source,
            string directory,
            bool copy) =>
            new(
                sequence,
                copy ? FilePrimitive.CopyFile : FilePrimitive.MoveFile,
                source.RelativePath,
                $"{directory}\\{source.RelativePath.Split('\\')[^1]}",
                new SourceFileFingerprint(
                    source.EntryIdentity!,
                    source.Length!.Value,
                    source.LastWriteUtc,
                    source.ContentHash),
                DestinationPrecondition.Absent,
                copy ? ExpectedSourceState.Unchanged : ExpectedSourceState.Absent,
                ExpectedDestinationState.FileMatchesSource);

        private static FolderSnapshotEntry Entry(FolderSnapshot snapshot, string path) =>
            snapshot.Entries.Single(entry =>
                string.Equals(entry.RelativePath, path, StringComparison.Ordinal));

        private static readonly GrantCapability[] Capabilities =
        [
            GrantCapability.Enumerate,
            GrantCapability.ReadMetadata,
            GrantCapability.ReadContentHash,
            GrantCapability.CreateDirectory,
            GrantCapability.MoveWithinRoot,
            GrantCapability.CopyWithinRoot,
        ];
    }

    private sealed record PreparedUndo(RecoveryPlan Plan, UndoExecutionRequest Request);

    public sealed class MutableAuthority : IExecutionAuthoritySource
    {
        private readonly SkillVersion currentSkillVersion;
        private readonly DateTimeOffset nowUtc;
        private LocalFolderGrant currentGrant;

        public MutableAuthority(
            SkillVersion skillVersion,
            LocalFolderGrant grant,
            DateTimeOffset nowUtc)
        {
            currentSkillVersion = skillVersion;
            currentGrant = grant;
            this.nowUtc = nowUtc;
        }

        public ValueTask<ExecutionAuthorityState?> ReadCurrentAsync(
            SkillId skillId,
            SkillVersionNumber skillVersion,
            GrantId grantId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ExecutionAuthorityState?>(
                new ExecutionAuthorityState(currentSkillVersion, currentGrant));
        }

        public void Revoke() =>
            currentGrant = currentGrant.Revoke(nowUtc, "user_revoked").Value!;
    }

    public sealed class MultiExecutionJournalStore : IExecutionJournalStore
    {
        private readonly Dictionary<ExecutionId, List<ExecutionJournalEvent>> journals = [];
        private readonly HashSet<ApprovalId> approvals = [];

        public IEnumerable<ExecutionId> ExecutionIds => journals.Keys;

        public ExecutionReceipt? Receipt { get; private set; }

        public RecoveryExecutionReceipt? RecoveryReceipt { get; private set; }

        public IReadOnlyList<ExecutionJournalEvent> Events(ExecutionId executionId) =>
            journals[executionId];

        public ValueTask<JournalWriteResult> CreateAsync(
            ExecutionJournal journal,
            PlanApproval consumedApproval,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (consumedApproval.State != PlanApprovalState.Consumed ||
                !approvals.Add(consumedApproval.Id) ||
                !journals.TryAdd(journal.ExecutionId, [journal.Events[0]]))
            {
                return ValueTask.FromResult(
                    JournalWriteResult.Failure("persistence.open_rejected"));
            }

            return ValueTask.FromResult(JournalWriteResult.Success);
        }

        public ValueTask<JournalWriteResult> AppendAsync(
            ExecutionJournalEvent journalEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!journals.TryGetValue(
                    journalEvent.ExecutionId,
                    out List<ExecutionJournalEvent>? events) ||
                journalEvent.EventSequence != events.Count + 1L)
            {
                return ValueTask.FromResult(
                    JournalWriteResult.Failure("persistence.sequence_rejected"));
            }

            events.Add(journalEvent);
            return ValueTask.FromResult(JournalWriteResult.Success);
        }

        public ValueTask<JournalWriteResult> StoreReceiptAsync(
            ExecutionReceipt receipt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Receipt = receipt;
            return ValueTask.FromResult(JournalWriteResult.Success);
        }

        public ValueTask<JournalWriteResult> StoreRecoveryReceiptAsync(
            RecoveryExecutionReceipt receipt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecoveryReceipt = receipt;
            return ValueTask.FromResult(JournalWriteResult.Success);
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class RecordingRecoveryFaultInjector(
        Action<RecoveryExecutionBoundaryContext> action) : IRecoveryExecutionFaultInjector
    {
        public void Reach(RecoveryExecutionBoundaryContext context) => action(context);
    }

    private sealed class InjectedRecoveryCrashException : Exception;

    private sealed class PortableRecoveryProbe : IFileSystemPathProbe
    {
        public FileSystemPathProbeResult Probe(string absolutePath)
        {
            string fullPath = Path.GetFullPath(absolutePath);
            FileSystemInfo? info = Directory.Exists(fullPath)
                ? new DirectoryInfo(fullPath)
                : File.Exists(fullPath)
                    ? new FileInfo(fullPath)
                    : null;
            if (info is null)
            {
                return FileSystemPathProbeResult.Failed(
                    FileSystemPathProbeStatus.NotFound,
                    "portable.not_found");
            }

            info.Refresh();
            bool directory = info is DirectoryInfo;
            bool reparse =
                (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                info.LinkTarget is not null;
            byte[] material = directory
                ? Encoding.UTF8.GetBytes(fullPath)
                : File.ReadAllBytes(fullPath);
            string identity = $"portable-{(directory ? "dir" : "file")}:" +
                Convert.ToHexStringLower(SHA256.HashData(material));
            return FileSystemPathProbeResult.Found(
                fullPath,
                directory ? FileSystemEntryKind.Directory : FileSystemEntryKind.File,
                "portable-volume",
                identity,
                reparse,
                isLocalFixedDrive: true);
        }
    }
}
