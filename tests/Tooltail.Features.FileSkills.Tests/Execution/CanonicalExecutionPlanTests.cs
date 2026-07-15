using System.Text;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Execution;

namespace Tooltail.Features.FileSkills.Tests.Execution;

public sealed class CanonicalExecutionPlanTests
{
    [Fact]
    public void CanonicalEncodingMatchesGoldenV1Projection()
    {
        const string expected =
            """
            {"contractVersion":"tooltail.execution-plan/1","planId":"11111111-1111-1111-1111-111111111111","skill":{"id":"22222222-2222-2222-2222-222222222222","version":3,"specificationSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"grant":{"id":"33333333-3333-3333-3333-333333333333","rootIdentity":"winfs-v1:volume-a:root-a","actions":["create_directory","move_within_root","read_metadata"]},"createdUtc":"2026-07-16T04:00:00.0000000Z","expiresUtc":"2026-07-16T04:30:00.0000000Z","operations":[{"sequence":1,"primitive":"ensure_directory","sourceRelativePath":null,"destinationRelativePath":"Archive\\2026","destinationPrecondition":"absent","sourceFingerprint":null,"expectedSourceState":"not_applicable","expectedDestinationState":"directory_present"},{"sequence":2,"primitive":"move_file","sourceRelativePath":"Inbox\\Report.txt","destinationRelativePath":"Archive\\2026\\Report.txt","destinationPrecondition":"absent","sourceFingerprint":{"entryIdentity":"file-id-01","length":128,"lastWriteUtc":"2026-07-16T03:55:00.0000000Z","contentSha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"},"expectedSourceState":"absent","expectedDestinationState":"file_matches_source"}]}
            """;

        string actual = Encoding.UTF8.GetString(
            CanonicalExecutionPlan.Encode(ExecutionPlanFixture.Definition()));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CanonicalFingerprintMatchesGoldenSha256()
    {
        ExecutionPlan plan = ExecutionPlanFixture.Plan();

        Assert.Equal(
            "efe13270c9faee37ab2fd13d5ffc5294c005e79edd05d89768f31b6c469b1359",
            plan.Fingerprint.Value);
    }

    [Fact]
    public void CapabilityInputOrderDoesNotChangeFingerprint()
    {
        ExecutionPlan first = ExecutionPlanFixture.Plan();
        ExecutionPlan second = ExecutionPlanFixture.Plan(
            ExecutionPlanFixture.Definition(
                capabilities:
                [
                    GrantCapability.ReadMetadata,
                    GrantCapability.CreateDirectory,
                    GrantCapability.MoveWithinRoot,
                ]));

        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void EveryMaterialPlanMutationChangesFingerprint()
    {
        PlanFingerprint baseline = ExecutionPlanFixture.Plan().Fingerprint;
        PlannedFileOperation[] reversedSemantics =
        [
            new PlannedFileOperation(
                1,
                FilePrimitive.MoveFile,
                "Inbox\\Report.txt",
                "Archive\\2026\\Report.txt",
                new SourceFileFingerprint(
                    "file-id-01",
                    128,
                    ExecutionPlanFixture.Now.AddMinutes(-5),
                    new ContentHash(new string('b', 64))),
                DestinationPrecondition.Absent,
                ExpectedSourceState.Absent,
                ExpectedDestinationState.FileMatchesSource),
            new PlannedFileOperation(
                2,
                FilePrimitive.EnsureDirectory,
                null,
                "Archive\\2026",
                null,
                DestinationPrecondition.Absent,
                ExpectedSourceState.NotApplicable,
                ExpectedDestinationState.DirectoryPresent),
        ];
        ExecutionPlanDefinition[] mutations =
        [
            ExecutionPlanFixture.Definition(
                planId: new PlanId(Guid.Parse("11111111-1111-1111-1111-111111111112"))),
            ExecutionPlanFixture.Definition(
                skillId: new SkillId(Guid.Parse("22222222-2222-2222-2222-222222222223"))),
            ExecutionPlanFixture.Definition(skillVersion: new SkillVersionNumber(4)),
            ExecutionPlanFixture.Definition(specificationHash: new string('c', 64)),
            ExecutionPlanFixture.Definition(
                grantId: new GrantId(Guid.Parse("33333333-3333-3333-3333-333333333334"))),
            ExecutionPlanFixture.Definition(rootIdentity: "winfs-v1:volume-a:root-b"),
            ExecutionPlanFixture.Definition(
                capabilities:
                [
                    .. ExecutionPlanFixture.Capabilities,
                    GrantCapability.CopyWithinRoot,
                ]),
            ExecutionPlanFixture.Definition(createdUtc: ExecutionPlanFixture.Now.AddSeconds(1)),
            ExecutionPlanFixture.Definition(expiresUtc: ExecutionPlanFixture.Now.AddMinutes(29)),
            ExecutionPlanFixture.Definition(
                operations: ExecutionPlanFixture.Operations(source: "Inbox\\Other.txt")),
            ExecutionPlanFixture.Definition(
                operations: ExecutionPlanFixture.Operations(destination: "Archive\\2026\\Other.txt")),
            ExecutionPlanFixture.Definition(
                operations: ExecutionPlanFixture.Operations(sourceIdentity: "file-id-02")),
            ExecutionPlanFixture.Definition(
                operations: ExecutionPlanFixture.Operations(sourceLength: 129)),
            ExecutionPlanFixture.Definition(
                operations: ExecutionPlanFixture.Operations(contentHash: new string('c', 64))),
            ExecutionPlanFixture.Definition(operations: reversedSemantics),
        ];

        Assert.All(
            mutations,
            mutation => Assert.NotEqual(
                baseline,
                CanonicalExecutionPlan.Create(mutation).Value!.Fingerprint));
    }

    [Fact]
    public void UnknownPrimitiveAndUncoveredCapabilityFailClosed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PlannedFileOperation(
                1,
                (FilePrimitive)999,
                "source.txt",
                "destination.txt",
                new SourceFileFingerprint(
                    "file-id",
                    1,
                    ExecutionPlanFixture.Now,
                    contentHash: null),
                DestinationPrecondition.Absent,
                ExpectedSourceState.Absent,
                ExpectedDestinationState.FileMatchesSource));
        Assert.Throws<ArgumentException>(
            () => ExecutionPlanFixture.Definition(
                capabilities: [GrantCapability.CreateDirectory]));
    }

    [Theory]
    [InlineData("C:\\outside.txt", "safe.txt", "path.drive_relative")]
    [InlineData("source.txt", "..\\outside.txt", "path.traversal")]
    [InlineData("Report.txt", "report.txt", "path.case_only_change_unsupported")]
    public void PlanCreationRejectsUnsafeOrAmbiguousPaths(
        string source,
        string destination,
        string expectedCode)
    {
        ExecutionPlanDefinition definition = ExecutionPlanFixture.Definition(
            operations: ExecutionPlanFixture.Operations(
                source: source,
                destination: destination));

        var result = CanonicalExecutionPlan.Create(definition);

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedCode, result.Error?.Code);
    }

    [Fact]
    public void PlanDefinitionRejectsDefaultStructAuthorityIdentifiers()
    {
        Assert.Throws<ArgumentException>(
            () => ExecutionPlanFixture.Definition(planId: default(PlanId)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ExecutionPlanFixture.Definition(skillVersion: default(SkillVersionNumber)));
    }
}
