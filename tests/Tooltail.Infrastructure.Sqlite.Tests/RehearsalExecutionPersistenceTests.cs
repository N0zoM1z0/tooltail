using Tooltail.Application.Abstractions;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Features.FileSkills.Execution;
using Tooltail.Features.FileSkills.Rehearsal;

namespace Tooltail.Infrastructure.Sqlite.Tests;

public sealed class RehearsalExecutionPersistenceTests
{
    [Fact]
    public async Task PreparedRehearsalConsumesExactApprovalAndRetiresTemporaryGrant()
    {
        using SqlitePersistenceTestContext context =
            await SqlitePersistenceTestContext.CreateAsync();
        await context.SeedAuthorityAndSkillAsync(
            Tooltail.Domain.Skills.SkillLifecycleState.Draft);
        GrantId temporaryGrantId = new(
            Guid.Parse("44444444-4444-4444-8444-444444444444"));
        LocalFolderGrant temporaryGrant = LocalFolderGrant.Issue(
            temporaryGrantId,
            SqlitePersistenceTestContext.CompanionId,
            new ResourceRootIdentity("temporary-rehearsal-root"),
            context.Grant.Capabilities,
            SqlitePersistenceTestContext.Now.AddMinutes(2),
            SqlitePersistenceTestContext.Now.AddMinutes(40));
        ExecutionPlanDefinition definition = new(
            new PlanId(Guid.Parse("55555555-5555-4555-8555-555555555555")),
            SqlitePersistenceTestContext.SkillId,
            new Tooltail.Domain.Skills.SkillVersionNumber(1),
            new SkillSpecificationHash(context.SkillHash),
            temporaryGrant.Id,
            temporaryGrant.RootIdentity,
            temporaryGrant.Capabilities,
            SqlitePersistenceTestContext.Now.AddMinutes(3),
            SqlitePersistenceTestContext.Now.AddMinutes(30),
            [
                new PlannedFileOperation(
                    1,
                    FilePrimitive.EnsureDirectory,
                    sourceRelativePath: null,
                    "Invoices",
                    sourceFingerprint: null,
                    DestinationPrecondition.Absent,
                    ExpectedSourceState.NotApplicable,
                    ExpectedDestinationState.DirectoryPresent),
            ]);
        var created = CanonicalExecutionPlan.Create(definition);
        Assert.True(created.IsSuccess, created.Error?.Code);
        ExecutionPlan plan = created.Value!;
        PlanApproval active = PlanApproval.IssueRehearsal(
            new ApprovalId(Guid.Parse("66666666-6666-4666-8666-666666666666")),
            plan,
            SqlitePersistenceTestContext.Now.AddMinutes(4),
            SqlitePersistenceTestContext.Now.AddMinutes(20));
        FileSkillStateRehearsalExecutionPersistence persistence = new(
            context.StateStore);

        RehearsalPersistenceResult prepared = await persistence.PrepareAsync(
            temporaryGrant,
            plan,
            active);
        PlanApproval consumed = active.Consume(
            plan,
            SqlitePersistenceTestContext.Now.AddMinutes(5)).Value!;
        ExecutionId executionId = new(
            Guid.Parse("77777777-7777-4777-8777-777777777777"));
        ExecutionJournal journal = ExecutionJournal.Open(
            executionId,
            plan,
            SqlitePersistenceTestContext.Now.AddMinutes(5));
        JournalWriteResult opened = await context.JournalStore.CreateAsync(
            journal,
            consumed);
        RehearsalPersistenceResult retired = await persistence.RetireGrantAsync(
            temporaryGrant,
            SqlitePersistenceTestContext.Now.AddMinutes(6));
        await context.RestartAsync();
        StateReadResult<StoredPlanDocument> storedPlan =
            await context.StateStore.LoadPlanDocumentAsync(plan.Definition.Id);
        StateReadResult<FileSkillWorkspaceStateRecord> workspace =
            await context.StateStore.LoadWorkspaceStateAsync(
                SqlitePersistenceTestContext.CompanionId);
        ExecutionJournalReadResult storedJournal =
            await context.JournalStore.LoadJournalAsync(executionId);

        Assert.True(prepared.IsSuccess, prepared.ReasonCode);
        Assert.True(opened.IsSuccess, opened.FailureCode);
        Assert.True(retired.IsSuccess, retired.ReasonCode);
        Assert.True(storedPlan.IsSuccess, storedPlan.ReasonCode);
        Assert.Equal(plan.Fingerprint, storedPlan.Value!.Fingerprint);
        Assert.True(workspace.IsSuccess, workspace.ReasonCode);
        LocalFolderGrantStateRecord persistedTemporary = Assert.Single(
            workspace.Value!.Grants,
            grant => grant.Grant.Id == temporaryGrantId);
        Assert.Equal(ResourceGrantState.Revoked, persistedTemporary.Grant.State);
        Assert.Equal("rehearsal.completed", persistedTemporary.Grant.RevocationReason);
        Assert.True(storedJournal.IsSuccess, storedJournal.ReasonCode);
        Assert.Equal(plan.Fingerprint, storedJournal.Journal!.PlanFingerprint);
    }
}
