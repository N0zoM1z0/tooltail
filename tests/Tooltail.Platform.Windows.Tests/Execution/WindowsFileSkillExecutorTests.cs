using System.Security.Cryptography;
using Tooltail.Application.Abstractions;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Execution;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Features.FileSkills.Snapshots;
using Tooltail.Platform.Windows.FileSystem;

namespace Tooltail.Platform.Windows.Tests.Execution;

public sealed class WindowsFileSkillExecutorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 16, 4, 0, 0, TimeSpan.Zero);

    [WindowsFact]
    [Trait("Platform", "Windows")]
    public async Task NativePathIdentityAndSharedExecutorVerifyCopyOnOwnedFixture()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"tooltail-executor-{Guid.NewGuid():N}");
        string inbox = Path.Combine(directory, "Inbox");
        string review = Path.Combine(directory, "Review");
        Directory.CreateDirectory(inbox);
        Directory.CreateDirectory(review);
        string sourcePath = Path.Combine(inbox, "invoice.pdf");
        await File.WriteAllTextAsync(sourcePath, "synthetic-invoice");
        File.SetLastWriteTimeUtc(
            sourcePath,
            new DateTime(2026, 6, 15, 3, 2, 1, DateTimeKind.Utc));

        try
        {
            WindowsFileSystemPathProbe probe = new();
            WindowsPathSafetyService pathSafety = new(probe);
            PathSafetyResult<CanonicalLocalRoot> captured = pathSafety.CaptureRoot(directory);
            Assert.True(captured.IsSuccess, captured.Error?.ToString());
            CanonicalLocalRoot root = captured.Value!;
            FileSystemPathProbeResult sourceProbe = probe.Probe(sourcePath);
            Assert.Equal(FileSystemPathProbeStatus.Success, sourceProbe.Status);
            byte[] sourceBytes = await File.ReadAllBytesAsync(sourcePath);
            FileInfo source = new(sourcePath);
            SourceFileFingerprint sourceFingerprint = new(
                sourceProbe.EntryIdentity!,
                source.Length,
                AsUtc(source.LastWriteTimeUtc),
                new ContentHash(Convert.ToHexStringLower(SHA256.HashData(sourceBytes))));
            SkillId skillId = new(Guid.Parse("11111111-aaaa-4aaa-8aaa-111111111111"));
            GrantId grantId = new(Guid.Parse("22222222-bbbb-4bbb-8bbb-222222222222"));
            GrantCapability[] capabilities =
            [
                GrantCapability.Enumerate,
                GrantCapability.ReadMetadata,
                GrantCapability.ReadContentHash,
                GrantCapability.CopyWithinRoot,
            ];
            ExecutionPlanDefinition definition = new(
                new PlanId(Guid.Parse("33333333-cccc-4ccc-8ccc-333333333333")),
                skillId,
                new SkillVersionNumber(1),
                new SkillSpecificationHash(new string('a', 64)),
                grantId,
                root.Identity,
                capabilities,
                Now,
                Now.AddHours(1),
                [
                    new PlannedFileOperation(
                        1,
                        FilePrimitive.CopyFile,
                        "Inbox\\invoice.pdf",
                        "Review\\invoice.pdf",
                        sourceFingerprint,
                        DestinationPrecondition.Absent,
                        ExpectedSourceState.Unchanged,
                        ExpectedDestinationState.FileMatchesSource),
                ]);
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
                new CompanionId(Guid.Parse("44444444-dddd-4ddd-8ddd-444444444444")),
                root.Identity,
                capabilities,
                Now.AddMinutes(-1),
                Now.AddHours(2));
            FixedClock clock = new(Now.AddMinutes(2));
            PlanApproval approval = PlanApproval.Issue(
                new ApprovalId(Guid.Parse("55555555-eeee-4eee-8eee-555555555555")),
                plan,
                Now.AddMinutes(1),
                Now.AddMinutes(30));
            ExecutionAuthorization authorization = new PermissionGateway(clock)
                .Authorize(plan, skillVersion, grant, approval)
                .Value!;
            MemoryJournalStore store = new();
            FileSkillExecutor executor = new(
                clock,
                new FixedAuthoritySource(skillVersion, grant),
                store,
                pathSafety,
                new FolderSnapshotService(probe, clock));
            FileExecutionRequest request = new(
                new ExecutionId(Guid.Parse("66666666-ffff-4fff-8fff-666666666666")),
                new ReceiptId(Guid.Parse("77777777-aaaa-4aaa-8aaa-777777777777")),
                plan,
                authorization,
                root,
                FileExecutionMode.Production,
                TimeSpan.FromDays(7));

            FileExecutionResult result = await executor.ExecuteAsync(request);

            Assert.True(result.IsVerified, result.ReasonCode);
            Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(Path.Combine(review, "invoice.pdf")));
            Assert.Equal(sourceProbe.EntryIdentity, probe.Probe(sourcePath).EntryIdentity);
            Assert.NotEqual(
                sourceProbe.EntryIdentity,
                result.Receipt!.VerifiedSteps[0].Destination.EntryIdentity);
            Assert.Equal(
                StepRecoveryStatus.Verified,
                result.Journal!.AssessStep(1).Status);
            Assert.NotNull(store.Receipt);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FixedAuthoritySource(
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
                new ExecutionAuthorityState(skillVersion, grant));
        }
    }

    private sealed class MemoryJournalStore : IExecutionJournalStore
    {
        private readonly List<ExecutionJournalEvent> events = [];

        public ExecutionReceipt? Receipt { get; private set; }

        public ValueTask<JournalWriteResult> CreateAsync(
            ExecutionJournal journal,
            PlanApproval consumedApproval,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (consumedApproval.State != PlanApprovalState.Consumed || events.Count != 0)
            {
                return ValueTask.FromResult(
                    JournalWriteResult.Failure("persistence.open_rejected"));
            }

            events.Add(journal.Events[0]);
            return ValueTask.FromResult(JournalWriteResult.Success);
        }

        public ValueTask<JournalWriteResult> AppendAsync(
            ExecutionJournalEvent journalEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (journalEvent.EventSequence != events.Count + 1L)
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
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(JournalWriteResult.Failure("persistence.unsupported_receipt"));
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

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
