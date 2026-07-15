using System.Security.Cryptography;
using Tooltail.Application.Abstractions;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Execution;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Features.FileSkills.Snapshots;
using Tooltail.Testing;

namespace Tooltail.Features.FileSkills.Tests.Execution;

public sealed class FileSkillExecutorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 16, 4, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(FileExecutionMode.Production)]
    [InlineData(FileExecutionMode.Rehearsal)]
    public async Task SharedExecutorMovesAndVerifiesRealFiles(FileExecutionMode mode)
    {
        using ExecutionFixture fixture = ExecutionFixture.CreateMove();

        FileExecutionResult result = await fixture.ExecuteAsync(mode);

        Assert.True(result.IsVerified, result.ReasonCode);
        Assert.False(File.Exists(fixture.SourcePath));
        Assert.Equal(
            "invoice",
            File.ReadAllText(Path.Combine(fixture.RootPath, "Archive", "invoice.pdf")));
        Assert.Equal(2, result.Receipt!.VerifiedStepCount);
        Assert.Equal(2, result.Receipt.VerifiedSteps.Count);
        Assert.All(
            Enumerable.Range(1, 2),
            step => Assert.Equal(
                StepRecoveryStatus.Verified,
                result.Journal!.AssessStep(step).Status));
        Assert.Equal(
            result.Journal!.Events,
            fixture.JournalStore.Events);
        Assert.Same(result.Receipt, fixture.JournalStore.Receipt);
    }

    [Fact]
    public async Task ProductionAndRehearsalReachTheSameBoundariesAndFinalTree()
    {
        using ExecutionFixture production = ExecutionFixture.CreateMove();
        using ExecutionFixture rehearsal = ExecutionFixture.CreateMove();
        RecordingFaultInjector productionTrace = new();
        RecordingFaultInjector rehearsalTrace = new();

        FileExecutionResult productionResult = await production.ExecuteAsync(
            FileExecutionMode.Production,
            productionTrace);
        FileExecutionResult rehearsalResult = await rehearsal.ExecuteAsync(
            FileExecutionMode.Rehearsal,
            rehearsalTrace);

        Assert.True(productionResult.IsVerified);
        Assert.True(rehearsalResult.IsVerified);
        Assert.Equal(
            productionTrace.Boundaries,
            rehearsalTrace.Boundaries);
        Assert.Equal(
            RelativeTree(production.RootPath),
            RelativeTree(rehearsal.RootPath));
    }

    [Fact]
    public async Task UnexpectedConcurrentChangeFailsVerificationAndStops()
    {
        using ExecutionFixture fixture = ExecutionFixture.CreateCopy();
        RecordingFaultInjector injector = new(
            context =>
            {
                if (context is
                    {
                        Boundary: FileExecutionBoundary.AfterPrimitive,
                        StepSequence: 1,
                    })
                {
                    File.WriteAllText(
                        Path.Combine(fixture.RootPath, "unrelated.txt"),
                        "external");
                }
            });

        FileExecutionResult result = await fixture.ExecuteAsync(
            FileExecutionMode.Production,
            injector);

        Assert.Equal(FileExecutionStatus.RecoveryRequired, result.Status);
        Assert.Equal("execution.unexpected_entry_set_changed", result.ReasonCode);
        Assert.Equal(1, result.FailedStepSequence);
        Assert.Equal(
            StepRecoveryStatus.RecoveryRequired,
            result.Journal!.AssessStep(1).Status);
        Assert.Null(result.Receipt);
        Assert.True(File.Exists(Path.Combine(fixture.RootPath, "Review", "invoice.pdf")));
    }

    [Fact]
    public async Task RevocationBeforeNextStepLeavesItNotStarted()
    {
        using ExecutionFixture fixture = ExecutionFixture.CreateMove();
        RecordingFaultInjector injector = new(
            context =>
            {
                if (context is
                    {
                        Boundary: FileExecutionBoundary.StepVerifiedPersisted,
                        StepSequence: 1,
                    })
                {
                    fixture.AuthoritySource.Revoke();
                }
            });

        FileExecutionResult result = await fixture.ExecuteAsync(
            FileExecutionMode.Production,
            injector);

        Assert.Equal(FileExecutionStatus.AuthorityDenied, result.Status);
        Assert.Equal("permission.action_not_granted", result.ReasonCode);
        Assert.Equal(StepRecoveryStatus.Verified, result.Journal!.AssessStep(1).Status);
        Assert.Equal(StepRecoveryStatus.NotStarted, result.Journal.AssessStep(2).Status);
        Assert.True(Directory.Exists(Path.Combine(fixture.RootPath, "Archive")));
        Assert.True(File.Exists(fixture.SourcePath));
        Assert.Null(result.Receipt);
    }

    [Theory]
    [InlineData(FileExecutionBoundary.JournalOpened, 1, false)]
    [InlineData(FileExecutionBoundary.StepIntentPersisted, 2, false)]
    [InlineData(FileExecutionBoundary.AfterPrimitive, 2, true)]
    [InlineData(FileExecutionBoundary.MutationObservedPersisted, 3, true)]
    [InlineData(FileExecutionBoundary.StepCommittedPersisted, 4, true)]
    [InlineData(FileExecutionBoundary.StepVerifiedPersisted, 5, true)]
    [InlineData(FileExecutionBoundary.ReceiptPersisted, 5, true)]
    public async Task InjectedCrashPreservesExactDurablePrefixWithoutReplay(
        FileExecutionBoundary boundary,
        int expectedEventCount,
        bool expectedDestination)
    {
        using ExecutionFixture fixture = ExecutionFixture.CreateCopy();
        RecordingFaultInjector injector = new(
            context =>
            {
                if (context.Boundary == boundary)
                {
                    throw new InjectedCrashException();
                }
            });

        await Assert.ThrowsAsync<InjectedCrashException>(
            () => fixture.ExecuteAsync(FileExecutionMode.Production, injector));

        Assert.Equal(expectedEventCount, fixture.JournalStore.Events.Count);
        Assert.IsType<ExecutionOpenedEvent>(fixture.JournalStore.Events[0]);
        if (expectedEventCount >= 2)
        {
            Assert.IsType<StepIntentRecordedEvent>(fixture.JournalStore.Events[1]);
        }

        Assert.Equal(
            expectedDestination,
            File.Exists(Path.Combine(fixture.RootPath, "Review", "invoice.pdf")));
        Assert.Equal(
            boundary == FileExecutionBoundary.ReceiptPersisted,
            fixture.JournalStore.Receipt is not null);
    }

    [Fact]
    public async Task SourceChangedAfterIntentFailsBeforePrimitiveEvenWhenMetadataIsRestored()
    {
        using ExecutionFixture fixture = ExecutionFixture.CreateCopy();
        DateTime originalLastWrite = File.GetLastWriteTimeUtc(fixture.SourcePath);
        RecordingFaultInjector injector = new(
            context =>
            {
                if (context.Boundary == FileExecutionBoundary.BeforePrimitive)
                {
                    File.WriteAllText(fixture.SourcePath, "changed");
                    File.SetLastWriteTimeUtc(fixture.SourcePath, originalLastWrite);
                }
            });

        FileExecutionResult result = await fixture.ExecuteAsync(
            FileExecutionMode.Production,
            injector);

        Assert.Equal(FileExecutionStatus.RecoveryRequired, result.Status);
        Assert.Equal("path.identity_changed", result.ReasonCode);
        Assert.Equal(
            StepRecoveryStatus.RecoveryRequired,
            result.Journal!.AssessStep(1).Status);
        Assert.False(File.Exists(Path.Combine(fixture.RootPath, "Review", "invoice.pdf")));
    }

    [Fact]
    public async Task CancellationAfterIntentIsDurablyRecoveryRequiredWithoutMutation()
    {
        using ExecutionFixture fixture = ExecutionFixture.CreateCopy();
        using CancellationTokenSource cancellation = new();
        RecordingFaultInjector injector = new(
            context =>
            {
                if (context.Boundary == FileExecutionBoundary.BeforePrimitive)
                {
                    cancellation.Cancel();
                }
            });

        FileExecutionResult result = await fixture.ExecuteAsync(
            FileExecutionMode.Production,
            injector,
            cancellation.Token);

        Assert.Equal(FileExecutionStatus.RecoveryRequired, result.Status);
        Assert.Equal("execution.cancelled", result.ReasonCode);
        Assert.Equal(
            StepRecoveryStatus.RecoveryRequired,
            result.Journal!.AssessStep(1).Status);
        Assert.False(File.Exists(Path.Combine(fixture.RootPath, "Review", "invoice.pdf")));
    }

    private static string[] RelativeTree(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private sealed class ExecutionFixture : IDisposable
    {
        private readonly TemporaryDirectory temporaryDirectory;
        private readonly MutableClock clock;
        private readonly CanonicalLocalRoot root;
        private readonly ExecutionPlan plan;
        private readonly ExecutionAuthorization authorization;
        private readonly FileExecutionRequest productionRequest;
        private readonly FileExecutionRequest rehearsalRequest;
        private readonly PortableExecutionProbe probe;

        private ExecutionFixture(
            TemporaryDirectory temporaryDirectory,
            MutableClock clock,
            CanonicalLocalRoot root,
            LocalFolderGrant grant,
            SkillVersion skillVersion,
            ExecutionPlan plan,
            ExecutionAuthorization authorization,
            string sourcePath)
        {
            this.temporaryDirectory = temporaryDirectory;
            this.clock = clock;
            this.root = root;
            this.plan = plan;
            this.authorization = authorization;
            SourcePath = sourcePath;
            probe = new PortableExecutionProbe(root.CanonicalPath, root.EntryIdentity);
            AuthoritySource = new MutableAuthoritySource(skillVersion, grant, clock);
            JournalStore = new InMemoryExecutionJournalStore();
            productionRequest = Request(FileExecutionMode.Production, 6, 7);
            rehearsalRequest = Request(FileExecutionMode.Rehearsal, 8, 9);
        }

        public string RootPath => temporaryDirectory.Path;

        public string SourcePath { get; }

        public MutableAuthoritySource AuthoritySource { get; }

        public InMemoryExecutionJournalStore JournalStore { get; private set; }

        public static ExecutionFixture CreateMove()
        {
            TemporaryDirectory temporary = new();
            string source = temporary.CreateTextFile("Inbox/invoice.pdf", "invoice");
            SetStableLastWrite(source);
            return Create(
                temporary,
                source,
                [
                    new OperationDraft(
                        FilePrimitive.EnsureDirectory,
                        null,
                        "Archive"),
                    new OperationDraft(
                        FilePrimitive.MoveFile,
                        "Inbox\\invoice.pdf",
                        "Archive\\invoice.pdf"),
                ],
                [
                    GrantCapability.Enumerate,
                    GrantCapability.ReadMetadata,
                    GrantCapability.ReadContentHash,
                    GrantCapability.CreateDirectory,
                    GrantCapability.MoveWithinRoot,
                ]);
        }

        public static ExecutionFixture CreateCopy()
        {
            TemporaryDirectory temporary = new();
            string source = temporary.CreateTextFile("Inbox/invoice.pdf", "invoice");
            Directory.CreateDirectory(Path.Combine(temporary.Path, "Review"));
            SetStableLastWrite(source);
            return Create(
                temporary,
                source,
                [
                    new OperationDraft(
                        FilePrimitive.CopyFile,
                        "Inbox\\invoice.pdf",
                        "Review\\invoice.pdf"),
                ],
                [
                    GrantCapability.Enumerate,
                    GrantCapability.ReadMetadata,
                    GrantCapability.ReadContentHash,
                    GrantCapability.CopyWithinRoot,
                ]);
        }

        public async Task<FileExecutionResult> ExecuteAsync(
            FileExecutionMode mode,
            IFileExecutionFaultInjector? injector = null,
            CancellationToken cancellationToken = default)
        {
            JournalStore = new InMemoryExecutionJournalStore();
            FileSkillExecutor executor = new(
                clock,
                AuthoritySource,
                JournalStore,
                new WindowsPathSafetyService(probe),
                new FolderSnapshotService(probe, clock),
                injector);
            return await executor.ExecuteAsync(
                mode == FileExecutionMode.Production
                    ? productionRequest
                    : rehearsalRequest,
                cancellationToken);
        }

        public void Dispose() => temporaryDirectory.Dispose();

        private static ExecutionFixture Create(
            TemporaryDirectory temporary,
            string source,
            IReadOnlyList<OperationDraft> drafts,
            GrantCapability[] capabilities)
        {
            ResourceRootIdentity rootIdentity = new("portable-execution-root");
            const string rootEntryIdentity = "portable-root";
            CanonicalLocalRoot root = new(
                temporary.Path,
                rootIdentity,
                "portable-volume",
                rootEntryIdentity);
            PortableExecutionProbe probe = new(temporary.Path, rootEntryIdentity);
            FileSystemPathProbeResult sourceProbe = probe.Probe(source);
            FileInfo sourceInfo = new(source);
            ContentHash hash = new(
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(source))));
            SourceFileFingerprint fingerprint = new(
                sourceProbe.EntryIdentity!,
                sourceInfo.Length,
                AsUtc(sourceInfo.LastWriteTimeUtc),
                hash);
            PlannedFileOperation[] operations = drafts
                .Select((draft, index) => draft.Primitive == FilePrimitive.EnsureDirectory
                    ? new PlannedFileOperation(
                        index + 1,
                        draft.Primitive,
                        null,
                        draft.Destination,
                        null,
                        DestinationPrecondition.Absent,
                        ExpectedSourceState.NotApplicable,
                        ExpectedDestinationState.DirectoryPresent)
                    : new PlannedFileOperation(
                        index + 1,
                        draft.Primitive,
                        draft.Source,
                        draft.Destination,
                        fingerprint,
                        DestinationPrecondition.Absent,
                        draft.Primitive == FilePrimitive.CopyFile
                            ? ExpectedSourceState.Unchanged
                            : ExpectedSourceState.Absent,
                        ExpectedDestinationState.FileMatchesSource))
                .ToArray();
            SkillId skillId = new(Guid.Parse("10000000-0000-4000-8000-000000000001"));
            GrantId grantId = new(Guid.Parse("20000000-0000-4000-8000-000000000002"));
            ExecutionPlanDefinition definition = new(
                new PlanId(Guid.Parse("30000000-0000-4000-8000-000000000003")),
                skillId,
                new SkillVersionNumber(1),
                new SkillSpecificationHash(new string('a', 64)),
                grantId,
                rootIdentity,
                capabilities,
                Now,
                Now.AddHours(1),
                operations);
            ExecutionPlan plan = CanonicalExecutionPlan.Create(definition).Value!;
            SkillVersion skillVersion = new(
                skillId,
                new SkillVersionNumber(1),
                null,
                new string('a', 64),
                "0.1.0",
                "0.1.0",
                SkillLifecycleState.Approved,
                Now.AddMinutes(-2));
            LocalFolderGrant grant = LocalFolderGrant.Issue(
                grantId,
                new CompanionId(Guid.Parse("40000000-0000-4000-8000-000000000004")),
                rootIdentity,
                capabilities,
                Now.AddMinutes(-1),
                Now.AddHours(2));
            MutableClock clock = new(Now.AddMinutes(2));
            PlanApproval approval = PlanApproval.Issue(
                new ApprovalId(Guid.Parse("50000000-0000-4000-8000-000000000005")),
                plan,
                Now.AddMinutes(1),
                Now.AddMinutes(30));
            ExecutionAuthorization authorization = new PermissionGateway(clock)
                .Authorize(plan, skillVersion, grant, approval)
                .Value!;
            return new ExecutionFixture(
                temporary,
                clock,
                root,
                grant,
                skillVersion,
                plan,
                authorization,
                source);
        }

        private FileExecutionRequest Request(
            FileExecutionMode mode,
            int executionSuffix,
            int receiptSuffix) =>
            new(
                new ExecutionId(Guid.Parse($"60000000-0000-4000-8000-{executionSuffix:D12}")),
                new ReceiptId(Guid.Parse($"70000000-0000-4000-8000-{receiptSuffix:D12}")),
                plan,
                authorization,
                root,
                mode,
                TimeSpan.FromDays(7));

        private static void SetStableLastWrite(string path) =>
            File.SetLastWriteTimeUtc(
                path,
                new DateTime(2026, 6, 15, 3, 2, 1, DateTimeKind.Utc));
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class MutableAuthoritySource(
        SkillVersion skillVersion,
        LocalFolderGrant grant,
        IClock clock) : IExecutionAuthoritySource
    {
        private LocalFolderGrant currentGrant = grant;

        public void Revoke() =>
            currentGrant = currentGrant.Revoke(clock.UtcNow, "test_revocation").Value!;

        public ValueTask<ExecutionAuthorityState?> ReadCurrentAsync(
            SkillId skillId,
            SkillVersionNumber requestedVersion,
            GrantId grantId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ExecutionAuthorityState?>(
                new ExecutionAuthorityState(skillVersion, currentGrant));
        }
    }

    private sealed class InMemoryExecutionJournalStore : IExecutionJournalStore
    {
        private readonly HashSet<ApprovalId> consumedApprovals = [];
        private readonly List<ExecutionJournalEvent> events = [];

        public List<ExecutionJournalEvent> Events => events;

        public ExecutionReceipt? Receipt { get; private set; }

        public ValueTask<JournalWriteResult> CreateAsync(
            ExecutionJournal journal,
            PlanApproval consumedApproval,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (consumedApproval.State != PlanApprovalState.Consumed ||
                !consumedApprovals.Add(consumedApproval.Id) ||
                events.Count != 0)
            {
                return ValueTask.FromResult(
                    JournalWriteResult.Failure("persistence.approval_already_used"));
            }

            events.Add(journal.Events[0]);
            return ValueTask.FromResult(JournalWriteResult.Success);
        }

        public ValueTask<JournalWriteResult> AppendAsync(
            ExecutionJournalEvent journalEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (events.Count == 0 || journalEvent.EventSequence != events.Count + 1L)
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
    }

    private sealed class RecordingFaultInjector(
        Action<FileExecutionBoundaryContext>? action = null) : IFileExecutionFaultInjector
    {
        private readonly List<(FileExecutionBoundary Boundary, int? Step)> boundaries = [];

        public IReadOnlyList<(FileExecutionBoundary Boundary, int? Step)> Boundaries => boundaries;

        public void Reach(FileExecutionBoundaryContext context)
        {
            boundaries.Add((context.Boundary, context.StepSequence));
            action?.Invoke(context);
        }
    }

    private sealed class PortableExecutionProbe : IFileSystemPathProbe
    {
        private readonly string root;
        private readonly string rootEntryIdentity;

        public PortableExecutionProbe(string root, string rootEntryIdentity)
        {
            this.root = Path.GetFullPath(root);
            this.rootEntryIdentity = rootEntryIdentity;
        }

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
            bool isDirectory = info is DirectoryInfo;
            bool isReparse =
                (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                info.LinkTarget is not null;
            string identity = string.Equals(fullPath, root, StringComparison.Ordinal)
                ? rootEntryIdentity
                : isDirectory
                    ? $"portable-directory:{Path.GetRelativePath(root, fullPath)}"
                    : $"portable-file:{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fullPath)))}";
            return FileSystemPathProbeResult.Found(
                fullPath,
                isDirectory ? FileSystemEntryKind.Directory : FileSystemEntryKind.File,
                "portable-volume",
                identity,
                isReparse,
                isLocalFixedDrive: true);
        }
    }

    private sealed class InjectedCrashException : Exception;

    private sealed record OperationDraft(
        FilePrimitive Primitive,
        string? Source,
        string Destination);

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
