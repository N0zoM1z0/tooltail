using System.Security.Cryptography;
using System.Text;
using Tooltail.Application.Abstractions;
using Tooltail.Contracts.Skills;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Execution;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Features.FileSkills.Rehearsal;
using Tooltail.Features.FileSkills.Skills;
using Tooltail.Features.FileSkills.Snapshots;
using Tooltail.Features.FileSkills.Tests.Skills;
using Tooltail.Testing;

namespace Tooltail.Features.FileSkills.Tests.Rehearsal;

public sealed class SkillRehearsalServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 16, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DraftSkillRunsSharedExecutorOnCopiedOwnedFixtureAndCleansIt()
    {
        using RehearsalFixture fixture = new();

        SkillRehearsalResult result = await fixture.Service.RehearseAsync(fixture.Request);

        Assert.True(result.IsPassed, result.ReasonCode);
        Assert.Equal(fixture.SpecificationHash, result.SpecificationHash);
        Assert.NotNull(result.PlanFingerprint);
        Assert.Equal(FileExecutionMode.Rehearsal, result.Execution!.Mode);
        Assert.Equal(3, result.Execution.Receipt!.VerifiedStepCount);
        Assert.Equal(3, result.Execution.Receipt.VerifiedSteps.Count);
        Assert.All(
            Enumerable.Range(1, 3),
            step => Assert.Equal(
                StepRecoveryStatus.Verified,
                result.Execution.Journal!.AssessStep(step).Status));
        Assert.True(result.Cleanup!.IsSuccess);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.TemporaryBasePath));
        Assert.Equal(
            ["Inbox/invoice-a.pdf", "Inbox/invoice-b.pdf"],
            RelativeFiles(fixture.SourcePath));
        Assert.NotNull(fixture.JournalStore.Receipt);
    }

    [Fact]
    public async Task BoundedStagingFailureNeverExecutesAndStillRemovesWorkspace()
    {
        using RehearsalFixture fixture = new(
            new RehearsalFixtureLimits(maximumEntries: 10, maximumTotalFileBytes: 1));

        SkillRehearsalResult result = await fixture.Service.RehearseAsync(fixture.Request);

        Assert.Equal(SkillRehearsalStatus.StagingFailed, result.Status);
        Assert.Equal("rehearsal.fixture_byte_limit_exceeded", result.ReasonCode);
        Assert.Null(result.Execution);
        Assert.True(result.Cleanup!.IsSuccess);
        Assert.Empty(fixture.JournalStore.Events);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.TemporaryBasePath));
        Assert.Equal(
            ["Inbox/invoice-a.pdf", "Inbox/invoice-b.pdf"],
            RelativeFiles(fixture.SourcePath));
    }

    private static string[] RelativeFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private sealed class RehearsalFixture : IDisposable
    {
        private readonly TemporaryDirectory source = new();
        private readonly TemporaryDirectory temporaryBase = new();

        public RehearsalFixture(RehearsalFixtureLimits? limits = null)
        {
            string first = source.CreateTextFile("Inbox/invoice-a.pdf", "first invoice");
            string second = source.CreateTextFile("Inbox/invoice-b.pdf", "second invoice");
            SetStableLastWrite(first, second);
            Probe = new PortableMultiRootProbe();
            SourceRoot = Root(source.Path);
            CanonicalLocalRoot temporaryRoot = Root(temporaryBase.Path);
            WindowsPathSafetyService pathSafety = new(Probe);
            FixedClock clock = new(Now);
            GrantCapability[] capabilities =
            [
                GrantCapability.Enumerate,
                GrantCapability.ReadMetadata,
                GrantCapability.ReadContentHash,
                GrantCapability.CreateDirectory,
                GrantCapability.MoveWithinRoot,
            ];
            SourceGrant = LocalFolderGrant.Issue(
                new GrantId(Guid.Parse("22222222-2222-4222-8222-222222222222")),
                new CompanionId(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")),
                SourceRoot.Identity,
                capabilities,
                Now.AddMinutes(-2),
                Now.AddHours(1));
            Specification = SkillSpecFixture.Valid();
            SpecificationHash = CanonicalSkillSpec.ComputeHash(Specification);
            SkillVersion skillVersion = new(
                new SkillId(Specification.SkillId),
                new SkillVersionNumber(Specification.Version),
                null,
                SpecificationHash.Value,
                "0.1.0",
                "0.1.0",
                SkillLifecycleState.Draft,
                Now.AddHours(-1));
            JournalStore = new MemoryJournalStore();
            TooltailOwnedRehearsalWorkspaceFactory workspaceFactory = new(
                temporaryRoot,
                pathSafety,
                new FixedIdGenerator(
                    Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")));
            Service = new SkillRehearsalService(
                clock,
                workspaceFactory,
                JournalStore,
                pathSafety,
                new FolderSnapshotService(Probe, clock),
                fixtureLimits: limits);
            Request = new SkillRehearsalRequest(
                Specification,
                skillVersion,
                SourceRoot,
                SourceGrant,
                new GrantId(Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc")),
                new PlanId(Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd")),
                new ApprovalId(Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee")),
                new ExecutionId(Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff")),
                new ReceiptId(Guid.Parse("99999999-9999-4999-8999-999999999999")),
                TimeSpan.FromMinutes(10));
        }

        public string SourcePath => source.Path;

        public string TemporaryBasePath => temporaryBase.Path;

        public PortableMultiRootProbe Probe { get; }

        public CanonicalLocalRoot SourceRoot { get; }

        public LocalFolderGrant SourceGrant { get; }

        public SkillSpecContract Specification { get; }

        public SkillSpecificationHash SpecificationHash { get; }

        public MemoryJournalStore JournalStore { get; }

        public SkillRehearsalService Service { get; }

        public SkillRehearsalRequest Request { get; }

        public void Dispose()
        {
            temporaryBase.Dispose();
            source.Dispose();
        }

        private CanonicalLocalRoot Root(string path)
        {
            FileSystemPathProbeResult probed = Probe.Probe(path);
            return new CanonicalLocalRoot(
                Path.GetFullPath(path),
                new ResourceRootIdentity(
                    $"winfs-v1:{probed.VolumeIdentity}:{probed.EntryIdentity}"),
                probed.VolumeIdentity!,
                probed.EntryIdentity!);
        }

        private static void SetStableLastWrite(params string[] paths)
        {
            for (int index = 0; index < paths.Length; index++)
            {
                File.SetLastWriteTimeUtc(
                    paths[index],
                    new DateTime(2026, 6, 15, 3, 2, index + 1, DateTimeKind.Utc));
            }
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FixedIdGenerator(Guid id) : IIdGenerator
    {
        public Guid NewId() => id;
    }

    private sealed class MemoryJournalStore : IExecutionJournalStore
    {
        private readonly List<ExecutionJournalEvent> events = [];

        public List<ExecutionJournalEvent> Events => events;

        public ExecutionReceipt? Receipt { get; private set; }

        public ValueTask<JournalWriteResult> CreateAsync(
            ExecutionJournal journal,
            PlanApproval consumedApproval,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (events.Count != 0 || consumedApproval.State != PlanApprovalState.Consumed)
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

    private sealed class PortableMultiRootProbe : IFileSystemPathProbe
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
            byte[] identityMaterial = directory
                ? Encoding.UTF8.GetBytes(fullPath)
                : File.ReadAllBytes(fullPath);
            string identity = $"portable-{(directory ? "dir" : "file")}:" +
                Convert.ToHexStringLower(SHA256.HashData(identityMaterial));
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
