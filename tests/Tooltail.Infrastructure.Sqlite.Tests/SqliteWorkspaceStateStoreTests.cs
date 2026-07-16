using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Tooltail.Application.Abstractions;
using Tooltail.Application.FileSkills;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Domain.Teaching;

namespace Tooltail.Infrastructure.Sqlite.Tests;

public sealed class SqliteWorkspaceStateStoreTests
{
    [Fact]
    public async Task StartupCreatesOneLocalCompanionThenRestoresTheSameIdentity()
    {
        using SqlitePersistenceTestContext context =
            await SqlitePersistenceTestContext.CreateAsync();
        Guid generated = Guid.Parse("10101010-1010-4010-8010-101010101010");
        FileApprenticeStartupService service = new(
            context.StateStore,
            context.JournalStore,
            new StartupClock(),
            new StartupIds(generated));

        FileApprenticeStartupResult first = await service.InitializeAsync();
        await context.RestartAsync();
        service = new FileApprenticeStartupService(
            context.StateStore,
            context.JournalStore,
            new StartupClock(),
            new StartupIds(Guid.Parse("20202020-2020-4020-8020-202020202020")));
        FileApprenticeStartupResult restored = await service.InitializeAsync();

        Assert.True(first.IsReady, first.ReasonCode);
        Assert.True(first.CreatedCompanion);
        Assert.Equal("startup.first_run_ready", first.ReasonCode);
        Assert.Equal(generated, first.Workspace!.Companion.Id.Value);
        Assert.True(restored.IsReady, restored.ReasonCode);
        Assert.False(restored.CreatedCompanion);
        Assert.Equal("startup.persisted_state_ready", restored.ReasonCode);
        Assert.Equal(generated, restored.Workspace!.Companion.Id.Value);
        Assert.Empty(restored.Recovery!.Candidates);
    }

    [Fact]
    public async Task CompanionAndImmutableSkillVersionListsSupportStartupAndExport()
    {
        using SqlitePersistenceTestContext context =
            await SqlitePersistenceTestContext.CreateAsync();
        await context.SeedAuthorityAndSkillAsync();
        const string secondJson = "{\"version\":2}";
        string secondHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(secondJson)));
        SkillVersion second = new(
            SqlitePersistenceTestContext.SkillId,
            new SkillVersionNumber(2),
            new SkillVersionNumber(1),
            secondHash,
            "0.1.0-test",
            "0.1.0-test",
            SkillLifecycleState.Draft,
            SqlitePersistenceTestContext.Now.AddMinutes(2));
        StateWriteResult stored = await context.StateStore.StoreSkillVersionAsync(
            new SkillVersionStateRecord(
                SqlitePersistenceTestContext.CompanionId,
                "Organize test files",
                SqlitePersistenceTestContext.Now,
                second,
                MakeCurrent: true,
                "tooltail.skill-spec/1",
                secondJson,
                "test.compiler",
                ApprovedUtc: null,
                SemanticDiffJson: "{}"));
        Assert.True(stored.IsSuccess, stored.FailureCode);
        await context.RestartAsync();

        StateReadResult<IReadOnlyList<CompanionStateRecord>> companions =
            await context.StateStore.ListCompanionsAsync();
        StateReadResult<IReadOnlyList<SkillVersionStateRecord>> versions =
            await context.StateStore.LoadSkillVersionsAsync(
                SqlitePersistenceTestContext.SkillId);

        Assert.True(companions.IsSuccess, companions.ReasonCode);
        Assert.Equal(SqlitePersistenceTestContext.CompanionId, Assert.Single(companions.Value!).Id);
        Assert.True(versions.IsSuccess, versions.ReasonCode);
        Assert.Collection(
            versions.Value!,
            first =>
            {
                Assert.Equal(1, first.Version.Number.Value);
                Assert.False(first.MakeCurrent);
            },
            current =>
            {
                Assert.Equal(2, current.Version.Number.Value);
                Assert.True(current.MakeCurrent);
                Assert.Equal(new SkillVersionNumber(1), current.Version.Parent);
            });
    }

    [Fact]
    public async Task WorkspaceStateRestoresCurrentAuthoritySkillAndLessonAfterRestart()
    {
        using SqlitePersistenceTestContext context =
            await SqlitePersistenceTestContext.CreateAsync();
        await context.SeedAuthorityAndSkillAsync();
        LocalFolderGrant revoked = context.Grant.Revoke(
            SqlitePersistenceTestContext.Now.AddMinutes(2),
            "user.revoked").Value!;
        StateWriteResult storedGrant = await context.StateStore.StoreLocalFolderGrantAsync(
            new LocalFolderGrantStateRecord(revoked, [1, 2, 3, 4]));
        Assert.True(storedGrant.IsSuccess, storedGrant.FailureCode);

        TeachingEpisode episode = TeachingEpisode.Start(
            new TeachingEpisodeId(
                Guid.Parse("44444444-4444-4444-8444-444444444444")),
            SqlitePersistenceTestContext.CompanionId,
            SqlitePersistenceTestContext.GrantId,
            SqlitePersistenceTestContext.Now.AddMinutes(1));
        StateWriteResult storedEpisode = await context.StateStore.StoreTeachingEpisodeAsync(
            new TeachingEpisodeStateRecord(
                episode,
                BaselineSnapshotId: null,
                FinalSnapshotId: null,
                ReconciliationSummaryJson: null,
                SqlitePersistenceTestContext.Now.AddHours(1),
                Examples: []));
        Assert.True(storedEpisode.IsSuccess, storedEpisode.FailureCode);
        await context.RestartAsync();

        StateReadResult<FileSkillWorkspaceStateRecord> loaded =
            await context.StateStore.LoadWorkspaceStateAsync(
                SqlitePersistenceTestContext.CompanionId);

        Assert.True(loaded.IsSuccess, loaded.ReasonCode);
        Assert.Equal("Test companion", loaded.Value!.Companion.DisplayName);
        LocalFolderGrantStateRecord grant = Assert.Single(loaded.Value.Grants);
        Assert.Equal(ResourceGrantState.Revoked, grant.Grant.State);
        Assert.Equal("user.revoked", grant.Grant.RevocationReason);
        Assert.Equal([1, 2, 3, 4], grant.ProtectedCanonicalRoot);
        Assert.True(Assert.Single(loaded.Value.CurrentSkills).MakeCurrent);
        TeachingEpisodeSummaryStateRecord lesson = Assert.Single(
            loaded.Value.TeachingEpisodes);
        Assert.Equal(PersistedTeachingEpisodeStatus.Started, lesson.Status);
        Assert.Equal(PersistedTeachingEvidenceStatus.Pending, lesson.EvidenceStatus);
        Assert.Empty(loaded.Value.Executions);
    }

    [Fact]
    public async Task WorkspaceStateReportsIncompleteExecutionWithoutReplayingIt()
    {
        using SqlitePersistenceTestContext context =
            await SqlitePersistenceTestContext.CreateAsync();
        await context.SeedAuthorityAndSkillAsync();
        ExecutionPlan plan = context.CreateStandardPlan(
            Guid.Parse("55555555-5555-4555-8555-555555555555"));
        Assert.True(
            (await context.StateStore.StoreExecutionPlanAsync(
                plan,
                SqlitePersistenceTestContext.CanonicalJson(plan))).IsSuccess);
        PlanApproval active = PlanApproval.Issue(
            new ApprovalId(Guid.Parse("66666666-6666-4666-8666-666666666666")),
            plan,
            SqlitePersistenceTestContext.Now.AddMinutes(3),
            SqlitePersistenceTestContext.Now.AddMinutes(30));
        Assert.True((await context.StateStore.StoreApprovalAsync(active)).IsSuccess);
        PlanApproval consumed = active.Consume(
            plan,
            SqlitePersistenceTestContext.Now.AddMinutes(4)).Value!;
        ExecutionJournal journal = ExecutionJournal.Open(
            new ExecutionId(Guid.Parse("77777777-7777-4777-8777-777777777777")),
            plan,
            SqlitePersistenceTestContext.Now.AddMinutes(4));
        Assert.True((await context.JournalStore.CreateAsync(journal, consumed)).IsSuccess);
        await context.RestartAsync();

        StateReadResult<FileSkillWorkspaceStateRecord> loaded =
            await context.StateStore.LoadWorkspaceStateAsync(
                SqlitePersistenceTestContext.CompanionId);
        ExecutionRecoveryScanResult recovery =
            await context.JournalStore.ScanRecoveryRequiredAsync();

        Assert.True(loaded.IsSuccess, loaded.ReasonCode);
        ExecutionSummaryStateRecord execution = Assert.Single(loaded.Value!.Executions);
        Assert.Equal(PersistedExecutionStatus.Running, execution.Status);
        Assert.False(execution.HasReceipt);
        Assert.Null(execution.CompletedUtc);
        ExecutionRecoveryCandidate candidate = Assert.Single(recovery.Candidates);
        Assert.Equal(execution.Id, candidate.ExecutionId);
        Assert.Equal("persistence.execution_incomplete", candidate.ReasonCode);
        Assert.Equal(StepRecoveryStatus.NotStarted, Assert.Single(candidate.Steps).Status);
    }

    [Fact]
    public async Task WorkspaceStateFailsClosedOnTamperedGrantFingerprint()
    {
        using SqlitePersistenceTestContext context =
            await SqlitePersistenceTestContext.CreateAsync();
        await context.SeedAuthorityAndSkillAsync();
        await using (SqliteConnection connection = await context.OpenRawAsync())
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "UPDATE resource_grants SET capabilities_json = '[\"enumerate\"]' " +
                "WHERE grant_id = $grant;";
            command.Parameters.AddWithValue(
                "$grant",
                SqlitePersistenceTestContext.GrantId.Value.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        StateReadResult<FileSkillWorkspaceStateRecord> loaded =
            await context.StateStore.LoadWorkspaceStateAsync(
                SqlitePersistenceTestContext.CompanionId);

        Assert.False(loaded.IsSuccess);
        Assert.Equal("persistence.grant_corrupt", loaded.ReasonCode);
    }

    private sealed class StartupClock : IClock
    {
        public DateTimeOffset UtcNow => SqlitePersistenceTestContext.Now;
    }

    private sealed class StartupIds(Guid value) : IIdGenerator
    {
        public Guid NewId() => value;
    }
}
