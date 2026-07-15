using System.Security.Cryptography;
using System.Text;
using Tooltail.Contracts.Skills;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Features.FileSkills.Planning;
using Tooltail.Features.FileSkills.Skills;
using Tooltail.Features.FileSkills.Snapshots;
using Tooltail.Features.FileSkills.Tests.Skills;

namespace Tooltail.Features.FileSkills.Tests.Planning;

public sealed class SkillPlannerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 16, 9, 0, 0, TimeSpan.Zero);

    private static readonly ResourceRootIdentity RootIdentity =
        new("winfs-v1:volume-plan:root-plan");

    [Fact]
    public void ExactDryRunIsPureOrderedAndInsensitiveToSnapshotInputOrder()
    {
        SkillSpecContract specification = SkillSpecFixture.Valid();
        FolderSnapshotEntry[] entries =
        [
            File("Inbox\\invoice-2.pdf", "second", "file-2"),
            Directory("Inbox", "dir-inbox"),
            File("Inbox\\invoice-1.pdf", "first", "file-1"),
        ];

        SkillPlanningResult first = Plan(specification, Snapshot(entries));
        SkillPlanningResult reordered = Plan(specification, Snapshot(entries.Reverse()));

        Assert.Equal(SkillPlanningStatus.Ready, first.Status);
        Assert.Equal(2, first.MatchedFileCount);
        Assert.Empty(first.Diagnostics);
        Assert.Equal(first.Plan!.Fingerprint, reordered.Plan!.Fingerprint);
        Assert.Equal(
            "42d27aa61ba96b4c2b21b2d3c46fc090866ccd65a6e0e782fb220577e5e548d7",
            first.Plan.Fingerprint.Value);
        Assert.Collection(
            first.Plan.Definition.Operations,
            operation =>
            {
                Assert.Equal(FilePrimitive.EnsureDirectory, operation.Primitive);
                Assert.Equal("Archive", operation.DestinationRelativePath);
                Assert.Equal(DestinationPrecondition.Absent, operation.DestinationPrecondition);
            },
            operation =>
            {
                Assert.Equal(FilePrimitive.MoveFile, operation.Primitive);
                Assert.Equal("Inbox\\invoice-1.pdf", operation.SourceRelativePath);
                Assert.Equal("Archive\\invoice-1.pdf", operation.DestinationRelativePath);
                Assert.Equal("file-1", operation.SourceFingerprint!.EntryIdentity);
                Assert.Equal(ExpectedSourceState.Absent, operation.ExpectedSourceState);
            },
            operation =>
            {
                Assert.Equal(FilePrimitive.MoveFile, operation.Primitive);
                Assert.Equal("Inbox\\invoice-2.pdf", operation.SourceRelativePath);
                Assert.Equal("Archive\\invoice-2.pdf", operation.DestinationRelativePath);
            });
    }

    [Fact]
    public void MissingDirectoryPrefixesAreExplicitAndOrderedBeforeFileEffects()
    {
        SkillSpecContract baseline = SkillSpecFixture.Valid();
        SkillSpecContract specification = baseline with
        {
            Variables =
            [
                .. baseline.Variables,
                new SkillVariableContract
                {
                    Name = "modifiedYear",
                    Source = SkillVariableSource.FileModifiedYear,
                    Transforms = [],
                },
            ],
            Steps =
            [
                new EnsureDirectoryStepContract
                {
                    StepId = "ensure_year",
                    DirectoryTemplate = "Archive/{{modifiedYear}}",
                },
                new MoveFileStepContract
                {
                    StepId = "move_file",
                    DestinationDirectoryTemplate = "Archive/{{modifiedYear}}",
                    DestinationFileNameTemplate =
                        "{{originalStem}}{{originalExtension}}",
                },
            ],
        };

        SkillPlanningResult result = Plan(
            specification,
            Snapshot(
            [
                Directory("Inbox", "dir-inbox"),
                File("Inbox\\invoice-1.pdf", "first", "file-1"),
            ]));

        Assert.Equal(SkillPlanningStatus.Ready, result.Status);
        Assert.Collection(
            result.Plan!.Definition.Operations,
            operation => Assert.Equal("Archive", operation.DestinationRelativePath),
            operation => Assert.Equal("Archive\\2026", operation.DestinationRelativePath),
            operation => Assert.Equal(
                "Archive\\2026\\invoice-1.pdf",
                operation.DestinationRelativePath));
    }

    [Fact]
    public void ExistingAndDuplicateDestinationsAreTypedConflicts()
    {
        SkillSpecContract baseline = SkillSpecFixture.Valid();
        SkillPlanningResult existing = Plan(
            baseline,
            Snapshot(
            [
                Directory("Archive", "dir-archive"),
                File("Archive\\invoice-1.pdf", "occupied", "occupied"),
                Directory("Inbox", "dir-inbox"),
                File("Inbox\\invoice-1.pdf", "first", "file-1"),
            ]));
        SkillVariableContract stem = baseline.Variables.Single(
            static variable => variable.Source == SkillVariableSource.OriginalStem);
        SkillSpecContract slugged = baseline with
        {
            Applicability = baseline.Applicability with
            {
                Match = baseline.Applicability.Match with
                {
                    Filename = new SkillFilenameMatchContract
                    {
                        Contains = "report",
                        CaseSensitive = false,
                    },
                },
            },
            Variables =
            [
                stem with { Transforms = [SkillVariableTransform.SlugHyphen] },
                baseline.Variables.Single(
                    static variable =>
                        variable.Source == SkillVariableSource.OriginalExtension),
            ],
        };
        SkillPlanningResult duplicate = Plan(
            slugged,
            Snapshot(
            [
                Directory("Inbox", "dir-inbox"),
                File("Inbox\\report one.pdf", "one", "file-1"),
                File("Inbox\\report-one.pdf", "two", "file-2"),
            ]));

        Assert.Equal(SkillPlanningStatus.Conflict, existing.Status);
        Assert.Contains(
            existing.Diagnostics,
            static diagnostic => diagnostic.Code == "planner.destination_exists");
        Assert.Equal(SkillPlanningStatus.Conflict, duplicate.Status);
        Assert.Contains(
            duplicate.Diagnostics,
            static diagnostic => diagnostic.Code == "planner.duplicate_destination");
        Assert.Null(existing.Plan);
        Assert.Null(duplicate.Plan);
    }

    [Fact]
    public void ChangedSourceEvidenceChangesTheExactPlanFingerprint()
    {
        SkillSpecContract specification = SkillSpecFixture.Valid();
        FolderSnapshot firstSnapshot = Snapshot(
        [
            Directory("Inbox", "dir-inbox"),
            File("Inbox\\invoice-1.pdf", "first", "file-1"),
        ]);
        FolderSnapshot changedSnapshot = Snapshot(
        [
            Directory("Inbox", "dir-inbox"),
            File("Inbox\\invoice-1.pdf", "first changed", "file-1"),
        ]);

        SkillPlanningResult first = Plan(specification, firstSnapshot);
        SkillPlanningResult changed = Plan(specification, changedSnapshot);

        Assert.Equal(SkillPlanningStatus.Ready, first.Status);
        Assert.Equal(SkillPlanningStatus.Ready, changed.Status);
        Assert.NotEqual(first.Plan!.Fingerprint, changed.Plan!.Fingerprint);
        Assert.NotEqual(
            first.Plan.Definition.Operations[1].SourceFingerprint,
            changed.Plan.Definition.Operations[1].SourceFingerprint);
    }

    [Fact]
    public void UnsafeUserBindingAndMissingSourceIdentityFailClosed()
    {
        SkillSpecContract baseline = SkillSpecFixture.Valid();
        SkillSpecContract parameterized = baseline with
        {
            Variables =
            [
                .. baseline.Variables,
                new SkillVariableContract
                {
                    Name = "bucket",
                    Source = SkillVariableSource.UserParameter,
                    Argument = "bucket",
                    Transforms = [],
                },
            ],
            Steps =
            [
                new EnsureDirectoryStepContract
                {
                    StepId = "ensure_bucket",
                    DirectoryTemplate = "{{bucket}}",
                },
                new MoveFileStepContract
                {
                    StepId = "move_file",
                    DestinationDirectoryTemplate = "{{bucket}}",
                    DestinationFileNameTemplate =
                        "{{originalStem}}{{originalExtension}}",
                },
            ],
        };
        FolderSnapshotEntry withoutIdentity = File(
            "Inbox\\invoice-1.pdf",
            "first",
            identity: null);
        SkillPlanningResult unsafeBinding = Plan(
            parameterized,
            Snapshot(
            [
                Directory("Inbox", "dir-inbox"),
                File("Inbox\\invoice-1.pdf", "first", "file-1"),
            ]),
            userParameters: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["bucket"] = "..\\Outside",
            });
        SkillPlanningResult noIdentity = Plan(
            baseline,
            Snapshot([Directory("Inbox", "dir-inbox"), withoutIdentity]));

        Assert.Equal(SkillPlanningStatus.Conflict, unsafeBinding.Status);
        Assert.Contains(
            unsafeBinding.Diagnostics,
            static diagnostic => diagnostic.Code == "path.traversal");
        Assert.Equal(SkillPlanningStatus.Conflict, noIdentity.Status);
        Assert.Contains(
            noIdentity.Diagnostics,
            static diagnostic => diagnostic.Code ==
                "planner.source_identity_unavailable");
    }

    [Fact]
    public void IncompleteSnapshotAndInsufficientGrantCannotProduceAPlan()
    {
        SkillSpecContract specification = SkillSpecFixture.Valid();
        FolderSnapshot complete = Snapshot(
        [
            Directory("Inbox", "dir-inbox"),
            File("Inbox\\invoice-1.pdf", "first", "file-1"),
        ]);
        FolderSnapshot incomplete = new(
            RootIdentity,
            complete.StartedUtc,
            complete.CompletedUtc,
            FolderSnapshotStatus.Cancelled,
            "snapshot.cancelled",
            complete.HashedBytes,
            complete.Entries);
        LocalFolderGrant insufficient = Grant(
        [
            GrantCapability.Enumerate,
            GrantCapability.ReadMetadata,
            GrantCapability.ReadContentHash,
            GrantCapability.CreateDirectory,
        ]);

        SkillPlanningResult incompleteResult = Plan(specification, incomplete);
        SkillPlanningResult deniedResult = Plan(
            specification,
            complete,
            insufficient);

        Assert.Equal(SkillPlanningStatus.IncompleteSnapshot, incompleteResult.Status);
        Assert.Equal(SkillPlanningStatus.AuthorityDenied, deniedResult.Status);
        Assert.Contains(
            deniedResult.Diagnostics,
            static diagnostic => diagnostic.Code == "planner.capability_missing");
    }

    [Fact]
    public void RenameAndCopyEmitTheirDistinctSourcePostconditions()
    {
        SkillSpecContract baseline = SkillSpecFixture.Valid();
        SkillSpecContract renameSpecification = baseline with
        {
            Steps =
            [
                new RenameFileStepContract
                {
                    StepId = "rename_file",
                    DestinationFileNameTemplate =
                        "filed-{{originalStem}}{{originalExtension}}",
                },
            ],
        };
        SkillSpecContract copySpecification = baseline with
        {
            Steps =
            [
                baseline.Steps[0],
                new CopyFileStepContract
                {
                    StepId = "copy_file",
                    DestinationDirectoryTemplate = "Archive",
                    DestinationFileNameTemplate =
                        "{{originalStem}}{{originalExtension}}",
                },
            ],
        };
        FolderSnapshot snapshot = Snapshot(
        [
            Directory("Inbox", "dir-inbox"),
            File("Inbox\\invoice-1.pdf", "first", "file-1"),
        ]);
        SkillPlanningResult rename = Plan(
            renameSpecification,
            snapshot,
            Grant(
            [
                GrantCapability.Enumerate,
                GrantCapability.ReadMetadata,
                GrantCapability.ReadContentHash,
                GrantCapability.Rename,
            ]));
        SkillPlanningResult copy = Plan(
            copySpecification,
            snapshot,
            Grant(
            [
                GrantCapability.Enumerate,
                GrantCapability.ReadMetadata,
                GrantCapability.ReadContentHash,
                GrantCapability.CreateDirectory,
                GrantCapability.CopyWithinRoot,
            ]));

        PlannedFileOperation renameOperation = Assert.Single(
            rename.Plan!.Definition.Operations);
        Assert.Equal(FilePrimitive.RenameFile, renameOperation.Primitive);
        Assert.Equal(
            "Inbox\\filed-invoice-1.pdf",
            renameOperation.DestinationRelativePath);
        Assert.Equal(ExpectedSourceState.Absent, renameOperation.ExpectedSourceState);
        PlannedFileOperation copyOperation = copy.Plan!.Definition.Operations[1];
        Assert.Equal(FilePrimitive.CopyFile, copyOperation.Primitive);
        Assert.Equal(ExpectedSourceState.Unchanged, copyOperation.ExpectedSourceState);
    }

    [Fact]
    public void ReparseAncestorMissingDestinationParentAndOperationLimitFailClosed()
    {
        SkillSpecContract baseline = SkillSpecFixture.Valid();
        FolderSnapshotEntry linkedInbox = new(
            "Inbox",
            SnapshotEntryKind.Directory,
            length: null,
            Now.AddDays(-2),
            Now.AddDays(-1),
            FileAttributes.Directory | FileAttributes.ReparsePoint,
            isReparsePoint: true,
            "volume-plan",
            "dir-link",
            SnapshotContentHashStatus.NotApplicable,
            contentHash: null);
        SkillPlanningResult reparse = Plan(
            baseline,
            Snapshot(
            [
                linkedInbox,
                File("Inbox\\invoice-1.pdf", "first", "file-1"),
            ]));
        SkillSpecContract noEnsure = baseline with
        {
            Steps = [baseline.Steps[1]],
        };
        SkillPlanningResult missingParent = Plan(
            noEnsure,
            Snapshot(
            [
                Directory("Inbox", "dir-inbox"),
                File("Inbox\\invoice-1.pdf", "first", "file-1"),
            ]));
        FolderSnapshot boundedSnapshot = Snapshot(
        [
            Directory("Inbox", "dir-inbox"),
            File("Inbox\\invoice-1.pdf", "first", "file-1"),
        ]);
        SkillPlanningRequest boundedRequest = new(
            new PlanId(Guid.Parse("55555555-5555-4555-8555-555555555555")),
            baseline,
            CanonicalSkillSpec.ComputeHash(baseline),
            Grant(),
            boundedSnapshot,
            Now,
            Now.AddMinutes(20));
        SkillPlanningLimits oneOperation = new(
            maximumMatches: 10,
            maximumOperations: 1,
            maximumUserParameters: 4,
            maximumSnapshotAge: TimeSpan.FromMinutes(5),
            maximumPlanLifetime: TimeSpan.FromMinutes(30));
        SkillPlanningResult limited = new SkillPlanner(oneOperation).DryRun(boundedRequest);

        Assert.Contains(
            reparse.Diagnostics,
            static diagnostic => diagnostic.Code == "planner.reparse_ancestor");
        Assert.Contains(
            missingParent.Diagnostics,
            static diagnostic =>
                diagnostic.Code == "planner.destination_parent_missing");
        Assert.Equal(SkillPlanningStatus.LimitExceeded, limited.Status);
        Assert.Contains(
            limited.Diagnostics,
            static diagnostic =>
                diagnostic.Code == "planner.operation_limit_exceeded");
    }

    private static SkillPlanningResult Plan(
        SkillSpecContract specification,
        FolderSnapshot snapshot,
        LocalFolderGrant? grant = null,
        IReadOnlyDictionary<string, string>? userParameters = null)
    {
        SkillSpecificationHash hash = CanonicalSkillSpec.ComputeHash(specification);
        SkillPlanningRequest request = new(
            new PlanId(Guid.Parse("55555555-5555-4555-8555-555555555555")),
            specification,
            hash,
            grant ?? Grant(),
            snapshot,
            Now,
            Now.AddMinutes(20),
            userParameters);
        return new SkillPlanner().DryRun(request);
    }

    private static LocalFolderGrant Grant(
        IEnumerable<GrantCapability>? capabilities = null) =>
        LocalFolderGrant.Issue(
            new GrantId(Guid.Parse("22222222-2222-4222-8222-222222222222")),
            new CompanionId(Guid.Parse("66666666-6666-4666-8666-666666666666")),
            RootIdentity,
            capabilities ??
            [
                GrantCapability.Enumerate,
                GrantCapability.ReadMetadata,
                GrantCapability.ReadContentHash,
                GrantCapability.CreateDirectory,
                GrantCapability.MoveWithinRoot,
            ],
            Now.AddHours(-2),
            Now.AddHours(1));

    private static FolderSnapshot Snapshot(IEnumerable<FolderSnapshotEntry> entries)
    {
        FolderSnapshotEntry[] materialized = entries.ToArray();
        long hashedBytes = materialized
            .Where(static entry =>
                entry.ContentHashStatus == SnapshotContentHashStatus.Computed)
            .Sum(static entry => entry.Length!.Value);
        return new FolderSnapshot(
            RootIdentity,
            Now.AddMinutes(-2),
            Now.AddMinutes(-1),
            FolderSnapshotStatus.Complete,
            reasonCode: null,
            hashedBytes,
            materialized);
    }

    private static FolderSnapshotEntry Directory(string path, string identity) =>
        new(
            path,
            SnapshotEntryKind.Directory,
            length: null,
            Now.AddDays(-2),
            Now.AddDays(-1),
            FileAttributes.Directory,
            isReparsePoint: false,
            "volume-plan",
            identity,
            SnapshotContentHashStatus.NotApplicable,
            contentHash: null);

    private static FolderSnapshotEntry File(
        string path,
        string content,
        string? identity)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        bool hasIdentity = identity is not null;
        return new FolderSnapshotEntry(
            path,
            SnapshotEntryKind.File,
            bytes.Length,
            Now.AddDays(-2),
            Now.AddMinutes(-10),
            FileAttributes.Archive,
            isReparsePoint: false,
            hasIdentity ? "volume-plan" : null,
            identity,
            SnapshotContentHashStatus.Computed,
            new ContentHash(Convert.ToHexStringLower(SHA256.HashData(bytes))));
    }
}
