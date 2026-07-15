using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Execution;

namespace Tooltail.Features.FileSkills.Tests.Execution;

internal static class ExecutionPlanFixture
{
    public static readonly DateTimeOffset Now =
        new(2026, 7, 16, 4, 0, 0, TimeSpan.Zero);

    public static readonly PlanId PlanId =
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    public static readonly SkillId SkillId =
        new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    public static readonly GrantId GrantId =
        new(Guid.Parse("33333333-3333-3333-3333-333333333333"));

    public static readonly ApprovalId ApprovalId =
        new(Guid.Parse("44444444-4444-4444-4444-444444444444"));

    public static readonly GrantCapability[] Capabilities =
    [
        GrantCapability.MoveWithinRoot,
        GrantCapability.ReadMetadata,
        GrantCapability.CreateDirectory,
    ];

    public static ExecutionPlanDefinition Definition(
        PlanId? planId = null,
        SkillId? skillId = null,
        SkillVersionNumber? skillVersion = null,
        string? specificationHash = null,
        GrantId? grantId = null,
        string? rootIdentity = null,
        IEnumerable<GrantCapability>? capabilities = null,
        DateTimeOffset? createdUtc = null,
        DateTimeOffset? expiresUtc = null,
        IEnumerable<PlannedFileOperation>? operations = null) =>
        new(
            planId ?? PlanId,
            skillId ?? SkillId,
            skillVersion ?? new SkillVersionNumber(3),
            new SkillSpecificationHash(specificationHash ?? new string('a', 64)),
            grantId ?? GrantId,
            new ResourceRootIdentity(rootIdentity ?? "winfs-v1:volume-a:root-a"),
            capabilities ?? Capabilities,
            createdUtc ?? Now,
            expiresUtc ?? Now.AddMinutes(30),
            operations ?? Operations());

    public static PlannedFileOperation[] Operations(
        string source = "Inbox\\Report.txt",
        string destination = "Archive\\2026\\Report.txt",
        string sourceIdentity = "file-id-01",
        long sourceLength = 128,
        string? contentHash = null) =>
    [
        new PlannedFileOperation(
            1,
            FilePrimitive.EnsureDirectory,
            sourceRelativePath: null,
            destinationRelativePath: "Archive\\2026",
            sourceFingerprint: null,
            DestinationPrecondition.Absent,
            ExpectedSourceState.NotApplicable,
            ExpectedDestinationState.DirectoryPresent),
        new PlannedFileOperation(
            2,
            FilePrimitive.MoveFile,
            source,
            destination,
            new SourceFileFingerprint(
                sourceIdentity,
                sourceLength,
                Now.AddMinutes(-5),
                new ContentHash(contentHash ?? new string('b', 64))),
            DestinationPrecondition.Absent,
            ExpectedSourceState.Absent,
            ExpectedDestinationState.FileMatchesSource),
    ];

    public static ExecutionPlan Plan(ExecutionPlanDefinition? definition = null) =>
        CanonicalExecutionPlan.Create(definition ?? Definition()).Value!;

    public static SkillVersion Skill(SkillLifecycleState lifecycle = SkillLifecycleState.Approved) =>
        new(
            SkillId,
            new SkillVersionNumber(3),
            new SkillVersionNumber(2),
            new string('a', 64),
            "0.1.0",
            "0.1.0",
            lifecycle,
            Now.AddDays(-1));

    public static LocalFolderGrant Grant(
        IEnumerable<GrantCapability>? capabilities = null) =>
        LocalFolderGrant.Issue(
            GrantId,
            new CompanionId(Guid.Parse("55555555-5555-5555-5555-555555555555")),
            new ResourceRootIdentity("winfs-v1:volume-a:root-a"),
            capabilities ?? Capabilities,
            Now.AddDays(-1),
            Now.AddDays(1));

    public static PlanApproval Approval(ExecutionPlan plan) =>
        PlanApproval.Issue(ApprovalId, plan, Now.AddMinutes(1), Now.AddMinutes(20));

    public static PlanApproval RehearsalApproval(ExecutionPlan plan) =>
        PlanApproval.IssueRehearsal(
            ApprovalId,
            plan,
            Now.AddMinutes(1),
            Now.AddMinutes(20));
}
