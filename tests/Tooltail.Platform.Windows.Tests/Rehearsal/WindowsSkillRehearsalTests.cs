using Tooltail.Application.Abstractions;
using Tooltail.Contracts.Json;
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
using Tooltail.Platform.Windows.FileSystem;

namespace Tooltail.Platform.Windows.Tests.Rehearsal;

public sealed class WindowsSkillRehearsalTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 16, 4, 0, 0, TimeSpan.Zero);

    [WindowsFact]
    [Trait("Platform", "Windows")]
    public async Task DraftRehearsalUsesNativeIdentitiesAndRemovesOwnedWorkspace()
    {
        string fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            $"tooltail-rehearsal-native-{Guid.NewGuid():N}");
        string sourcePath = Path.Combine(fixtureRoot, "source");
        string temporaryPath = Path.Combine(fixtureRoot, "owned-temp");
        Directory.CreateDirectory(sourcePath);
        Directory.CreateDirectory(temporaryPath);
        string invoice = Path.Combine(sourcePath, "invoice-native.pdf");
        await File.WriteAllTextAsync(invoice, "native rehearsal fixture");
        File.SetLastWriteTimeUtc(
            invoice,
            new DateTime(2026, 6, 15, 3, 2, 1, DateTimeKind.Utc));

        try
        {
            WindowsFileSystemPathProbe probe = new();
            WindowsPathSafetyService pathSafety = new(probe);
            CanonicalLocalRoot sourceRoot = Capture(pathSafety, sourcePath);
            CanonicalLocalRoot temporaryRoot = Capture(pathSafety, temporaryPath);
            SkillSpecContract specification = ReadExampleSpecification();
            SkillSpecificationHash specificationHash =
                CanonicalSkillSpec.ComputeHash(specification);
            GrantCapability[] capabilities =
            [
                GrantCapability.Enumerate,
                GrantCapability.ReadMetadata,
                GrantCapability.ReadContentHash,
                GrantCapability.CreateDirectory,
                GrantCapability.MoveWithinRoot,
            ];
            LocalFolderGrant sourceGrant = LocalFolderGrant.Issue(
                new GrantId(specification.Applicability.RootGrantId),
                new CompanionId(Guid.Parse("aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa")),
                sourceRoot.Identity,
                capabilities,
                Now.AddMinutes(-1),
                Now.AddHours(1));
            SkillVersion skillVersion = new(
                new SkillId(specification.SkillId),
                new SkillVersionNumber(specification.Version),
                null,
                specificationHash.Value,
                "0.1.0",
                "0.1.0",
                SkillLifecycleState.Draft,
                Now.AddHours(-1));
            FixedClock clock = new(Now);
            MemoryJournalStore store = new();
            SkillRehearsalService service = new(
                clock,
                new TooltailOwnedRehearsalWorkspaceFactory(
                    temporaryRoot,
                    pathSafety,
                    new FixedIdGenerator(
                        Guid.Parse("bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb"))),
                store,
                pathSafety,
                new FolderSnapshotService(probe, clock));
            SkillRehearsalRequest request = new(
                specification,
                skillVersion,
                sourceRoot,
                sourceGrant,
                new GrantId(Guid.Parse("cccccccc-3333-4333-8333-cccccccccccc")),
                new PlanId(Guid.Parse("dddddddd-4444-4444-8444-dddddddddddd")),
                new ApprovalId(Guid.Parse("eeeeeeee-5555-4555-8555-eeeeeeeeeeee")),
                new ExecutionId(Guid.Parse("ffffffff-6666-4666-8666-ffffffffffff")),
                new ReceiptId(Guid.Parse("99999999-7777-4777-8777-999999999999")),
                TimeSpan.FromMinutes(10));

            SkillRehearsalResult result = await service.RehearseAsync(request);

            Assert.True(result.IsPassed, result.ReasonCode);
            Assert.Equal(2, result.Execution!.Receipt!.VerifiedStepCount);
            Assert.True(File.Exists(invoice));
            Assert.False(Directory.Exists(Path.Combine(sourcePath, "Invoices")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(temporaryPath));
            Assert.NotNull(store.Receipt);
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private static CanonicalLocalRoot Capture(
        WindowsPathSafetyService pathSafety,
        string path)
    {
        PathSafetyResult<CanonicalLocalRoot> captured = pathSafety.CaptureRoot(path);
        Assert.True(captured.IsSuccess, captured.Error?.ToString());
        return captured.Value!;
    }

    private static SkillSpecContract ReadExampleSpecification()
    {
        string root = FindRepositoryRoot();
        byte[] json = File.ReadAllBytes(
            Path.Combine(root, "docs", "examples", "file-skill.example.json"));
        ContractParseResult<SkillSpecContract> parsed = ContractJson.ParseSkillSpec(json);
        Assert.True(parsed.IsSuccess, parsed.Error?.ToString());
        return parsed.Value!;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tooltail.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Tooltail.sln.");
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

        public ExecutionReceipt? Receipt { get; private set; }

        public ValueTask<JournalWriteResult> CreateAsync(
            ExecutionJournal journal,
            PlanApproval consumedApproval,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (events.Count != 0 ||
                consumedApproval.State != PlanApprovalState.Consumed ||
                consumedApproval.Purpose != PlanApprovalPurpose.Rehearsal)
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
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Requires a Windows host.";
            }
        }
    }
}
