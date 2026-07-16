using Tooltail.Application.Abstractions;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Execution;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Features.FileSkills.Snapshots;
using Tooltail.Features.FileSkills.Undo;
using Tooltail.Platform.Windows.FileSystem;

namespace Tooltail.Platform.Windows.Tests.Undo;

public sealed class WindowsFileRecoveryTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 16, 7, 0, 0, TimeSpan.Zero);

    [WindowsFact]
    [Trait("Platform", "Windows")]
    public async Task NativeIdentityCopyExecutionAndUndoRestoreExactFixture()
    {
        string fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            $"tooltail-undo-native-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(fixtureRoot, "Inbox"));
        string source = Path.Combine(fixtureRoot, "Inbox", "invoice.pdf");
        await File.WriteAllTextAsync(source, "native undo fixture");
        File.SetLastWriteTimeUtc(
            source,
            new DateTime(2026, 6, 15, 3, 2, 1, DateTimeKind.Utc));

        try
        {
            FixedClock clock = new(Now);
            WindowsFileSystemPathProbe probe = new();
            WindowsPathSafetyService pathSafety = new(probe);
            CanonicalLocalRoot root = pathSafety.CaptureRoot(fixtureRoot).Value!;
            GrantCapability[] capabilities =
            [
                GrantCapability.Enumerate,
                GrantCapability.ReadMetadata,
                GrantCapability.ReadContentHash,
                GrantCapability.CreateDirectory,
                GrantCapability.CopyWithinRoot,
            ];
            LocalFolderGrant grant = LocalFolderGrant.Issue(
                new GrantId(Guid.Parse("11111111-1111-4111-8111-111111111111")),
                new CompanionId(Guid.Parse("22222222-2222-4222-8222-222222222222")),
                root.Identity,
                capabilities,
                Now.AddDays(-1),
                Now.AddDays(8));
            SkillVersion skillVersion = new(
                new SkillId(Guid.Parse("33333333-3333-4333-8333-333333333333")),
                new SkillVersionNumber(1),
                null,
                new string('a', 64),
                "0.1.0",
                "0.1.0",
                SkillLifecycleState.Approved,
                Now.AddDays(-1));
            FixedAuthority authority = new(skillVersion, grant);
            MultiJournalStore store = new();
            FolderSnapshotService snapshots = new(probe, clock);
            FolderSnapshot initial = await snapshots.CaptureAsync(root, grant);
            FolderSnapshotEntry sourceEntry = initial.Entries.Single(
                static entry => entry.RelativePath == "Inbox\\invoice.pdf");
            ExecutionPlanDefinition executionDefinition = new(
                new PlanId(Guid.Parse("44444444-4444-4444-8444-444444444444")),
                skillVersion.SkillId,
                skillVersion.Number,
                new SkillSpecificationHash(skillVersion.SpecificationHash),
                grant.Id,
                root.Identity,
                grant.Capabilities,
                Now.AddMinutes(-1),
                Now.AddHours(1),
                [
                    new PlannedFileOperation(
                        1,
                        FilePrimitive.EnsureDirectory,
                        sourceRelativePath: null,
                        "Review",
                        sourceFingerprint: null,
                        DestinationPrecondition.Absent,
                        ExpectedSourceState.NotApplicable,
                        ExpectedDestinationState.DirectoryPresent),
                    new PlannedFileOperation(
                        2,
                        FilePrimitive.CopyFile,
                        sourceEntry.RelativePath,
                        "Review\\invoice.pdf",
                        new SourceFileFingerprint(
                            sourceEntry.EntryIdentity!,
                            sourceEntry.Length!.Value,
                            sourceEntry.LastWriteUtc,
                            sourceEntry.ContentHash),
                        DestinationPrecondition.Absent,
                        ExpectedSourceState.Unchanged,
                        ExpectedDestinationState.FileMatchesSource),
                ]);
            ExecutionPlan executionPlan =
                CanonicalExecutionPlan.Create(executionDefinition).Value!;
            PlanApproval executionApproval = PlanApproval.Issue(
                new ApprovalId(Guid.Parse("55555555-5555-4555-8555-555555555555")),
                executionPlan,
                Now,
                Now.AddMinutes(30));
            ExecutionAuthorization executionAuthorization = new PermissionGateway(clock)
                .Authorize(executionPlan, skillVersion, grant, executionApproval)
                .Value!;
            FileExecutionResult executed = await new FileSkillExecutor(
                clock,
                authority,
                store,
                pathSafety,
                snapshots).ExecuteAsync(
                new FileExecutionRequest(
                    new ExecutionId(Guid.Parse("66666666-6666-4666-8666-666666666666")),
                    new ReceiptId(Guid.Parse("77777777-7777-4777-8777-777777777777")),
                    executionPlan,
                    executionAuthorization,
                    root,
                    FileExecutionMode.Production,
                    TimeSpan.FromDays(7)));
            Assert.True(executed.IsVerified, executed.ReasonCode);

            string copied = Path.Combine(fixtureRoot, "Review", "invoice.pdf");
            DateTimeOffset verifiedLastWrite =
                executed.Receipt!.VerifiedSteps[1].Destination.LastWriteUtc;
            await File.WriteAllTextAsync(copied, "native undo fixturE");
            File.SetLastWriteTimeUtc(copied, verifiedLastWrite.UtcDateTime);
            FolderSnapshot changed = await snapshots.CaptureAsync(root, grant);
            UndoPlanningResult changedPlanning = new UndoPlanner().Plan(
                new UndoPlanningRequest(
                    new PlanId(Guid.Parse("88888888-8888-4888-8888-888888888888")),
                    executionPlan,
                    executed.Journal!,
                    executed.Receipt,
                    skillVersion,
                    grant,
                    changed,
                    Now,
                    Now.AddMinutes(10)));
            Assert.Equal(UndoPlanningStatus.Conflict, changedPlanning.Status);
            Assert.Equal("undo.source_changed", changedPlanning.ReasonCode);

            await File.WriteAllTextAsync(copied, "native undo fixture");
            File.SetLastWriteTimeUtc(copied, verifiedLastWrite.UtcDateTime);
            FolderSnapshot current = await snapshots.CaptureAsync(root, grant);
            UndoPlanningResult planning = new UndoPlanner().Plan(
                new UndoPlanningRequest(
                    new PlanId(Guid.Parse("88888888-8888-4888-8888-888888888888")),
                    executionPlan,
                    executed.Journal!,
                    executed.Receipt!,
                    skillVersion,
                    grant,
                    current,
                    Now,
                    Now.AddMinutes(10)));
            Assert.True(planning.IsReady, planning.ReasonCode);
            RecoveryPlan recoveryPlan = planning.Plan!;
            PlanApproval recoveryApproval = PlanApproval.IssueUndo(
                new ApprovalId(Guid.Parse("99999999-9999-4999-8999-999999999999")),
                recoveryPlan,
                Now,
                recoveryPlan.Definition.ExpiresUtc);
            ExecutionAuthorization recoveryAuthorization = new PermissionGateway(clock)
                .AuthorizeUndo(recoveryPlan, skillVersion, grant, recoveryApproval)
                .Value!;
            UndoExecutionResult undone = await new FileSkillExecutor(
                clock,
                authority,
                store,
                pathSafety,
                snapshots).ExecuteUndoAsync(
                new UndoExecutionRequest(
                    new ExecutionId(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")),
                    new ReceiptId(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")),
                    recoveryPlan,
                    recoveryAuthorization,
                    executionPlan,
                    executed.Journal!,
                    executed.Receipt!,
                    root));

            Assert.True(undone.IsVerified, undone.ReasonCode);
            Assert.True(File.Exists(source));
            Assert.False(Directory.Exists(Path.Combine(fixtureRoot, "Review")));
            Assert.Equal("native undo fixture", await File.ReadAllTextAsync(source));
            Assert.NotNull(store.RecoveryReceipt);
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FixedAuthority(
        SkillVersion currentSkillVersion,
        LocalFolderGrant currentGrant) : IExecutionAuthoritySource
    {
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
    }

    private sealed class MultiJournalStore : IExecutionJournalStore
    {
        private readonly Dictionary<ExecutionId, List<ExecutionJournalEvent>> journals = [];
        private readonly HashSet<ApprovalId> approvals = [];

        public RecoveryExecutionReceipt? RecoveryReceipt { get; private set; }

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

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute(
            [System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
            [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
            : base(sourceFilePath, (int)sourceLineNumber)
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Requires a Windows host.";
            }
        }
    }
}
