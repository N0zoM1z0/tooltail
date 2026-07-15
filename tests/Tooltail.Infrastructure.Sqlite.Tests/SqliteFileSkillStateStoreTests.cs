using System.Text;
using Microsoft.Data.Sqlite;
using Tooltail.Application.Abstractions;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Domain.Teaching;
using Tooltail.Features.FileSkills.Execution;

namespace Tooltail.Infrastructure.Sqlite.Tests;

public sealed class SqliteFileSkillStateStoreTests
{
    [Fact]
    public async Task LearningStateAdvancesMonotonicallyAndRestartsFromValidatedSkillHistory()
    {
        using SqlitePersistenceTestContext context =
            await SqlitePersistenceTestContext.CreateAsync();
        await context.SeedAuthorityAndSkillAsync();
        Guid baselineId = Guid.Parse("44444444-4444-4444-8444-444444444444");
        Guid finalId = Guid.Parse("55555555-5555-4555-8555-555555555555");
        await StoreSnapshotAsync(context, baselineId, minute: 2);
        await StoreSnapshotAsync(context, finalId, minute: 4);

        TeachingEpisodeId episodeId = new(
            Guid.Parse("66666666-6666-4666-8666-666666666666"));
        TeachingEpisode started = TeachingEpisode.Start(
            episodeId,
            SqlitePersistenceTestContext.CompanionId,
            SqlitePersistenceTestContext.GrantId,
            SqlitePersistenceTestContext.Now.AddMinutes(1));
        await AssertStoredAsync(context, Episode(started));
        TeachingEpisode baseline = started.CaptureBaseline().Value!;
        await AssertStoredAsync(context, Episode(baseline, baselineId));
        TeachingEpisode observing = baseline.BeginObservation().Value!;
        await AssertStoredAsync(context, Episode(observing, baselineId));
        TeachingEpisode stopped = observing.Stop(
            SqlitePersistenceTestContext.Now.AddMinutes(5)).Value!;
        await AssertStoredAsync(context, Episode(stopped, baselineId, finalId));
        TeachingEpisode reconciled = stopped.Reconcile(
            TeachingEvidenceState.Complete).Value!;
        DemonstrationExampleStateRecord example = new(
            new ExampleId(Guid.Parse("77777777-7777-4777-8777-777777777777")),
            FilePrimitive.EnsureDirectory,
            SourceRelativePath: null,
            "sorted",
            SourceFingerprintJson: null,
            "Create the sorted folder");
        await AssertStoredAsync(
            context,
            Episode(reconciled, baselineId, finalId, [example]));

        StateWriteResult regressed = await context.StateStore.StoreTeachingEpisodeAsync(
            Episode(started));
        Assert.False(regressed.IsSuccess);
        Assert.Equal("persistence.episode_state_regressed", regressed.FailureCode);

        SkillVersion stale = context.SkillVersion.TransitionTo(
            SkillLifecycleState.Stale).Value!;
        StateWriteResult storedStale = await context.StateStore.StoreSkillVersionAsync(
            context.SkillRecord(
                stale,
                SqlitePersistenceTestContext.Now.AddMinutes(1)));
        Assert.True(storedStale.IsSuccess, storedStale.FailureCode);

        await context.RestartAsync();
        StateReadResult<SkillVersionStateRecord> loaded =
            await context.StateStore.LoadSkillVersionAsync(
                SqlitePersistenceTestContext.SkillId,
                new SkillVersionNumber(1));

        Assert.True(loaded.IsSuccess, loaded.ReasonCode);
        Assert.Equal(SkillLifecycleState.Stale, loaded.Value!.Version.Lifecycle);
        Assert.Equal(
            SqlitePersistenceTestContext.Now.AddMinutes(1),
            loaded.Value.ApprovedUtc);
        await using SqliteConnection connection = await context.OpenRawAsync();
        Assert.Equal(
            1L,
            await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM demonstration_examples;"));
        Assert.Equal(
            "reconciled",
            await ScalarStringAsync(
                connection,
                "SELECT status FROM teaching_episodes;"));
    }

    [Fact]
    public async Task PlanStoreRejectsAValidJsonHashBoundToADifferentDomainDefinition()
    {
        using SqlitePersistenceTestContext context =
            await SqlitePersistenceTestContext.CreateAsync();
        await context.SeedAuthorityAndSkillAsync();
        ExecutionPlan canonical = context.CreateStandardPlan(
            Guid.Parse("88888888-8888-4888-8888-888888888888"));
        string canonicalJson = SqlitePersistenceTestContext.CanonicalJson(canonical);
        ExecutionPlanDefinition forgedDefinition = new(
            canonical.Definition.Id,
            canonical.Definition.SkillId,
            canonical.Definition.SkillVersion,
            canonical.Definition.SkillSpecificationHash,
            canonical.Definition.GrantId,
            canonical.Definition.RootIdentity,
            canonical.Definition.GrantedCapabilities,
            canonical.Definition.CreatedUtc,
            canonical.Definition.ExpiresUtc,
            [
                new PlannedFileOperation(
                    1,
                    FilePrimitive.EnsureDirectory,
                    sourceRelativePath: null,
                    "different-folder",
                    sourceFingerprint: null,
                    DestinationPrecondition.Absent,
                    ExpectedSourceState.NotApplicable,
                    ExpectedDestinationState.DirectoryPresent),
            ]);
        ExecutionPlan forged = new(forgedDefinition, canonical.Fingerprint);

        StateWriteResult rejected = await context.StateStore.StoreExecutionPlanAsync(
            forged,
            canonicalJson);
        StateWriteResult stored = await context.StateStore.StoreExecutionPlanAsync(
            canonical,
            canonicalJson);
        StateReadResult<StoredPlanDocument> loaded =
            await context.StateStore.LoadPlanDocumentAsync(canonical.Definition.Id);

        Assert.False(rejected.IsSuccess);
        Assert.Equal("persistence.plan_document_invalid", rejected.FailureCode);
        Assert.True(stored.IsSuccess, stored.FailureCode);
        Assert.True(loaded.IsSuccess, loaded.ReasonCode);
        Assert.Equal(canonical.Fingerprint, loaded.Value!.Fingerprint);
        Assert.Equal(canonicalJson, loaded.Value.CanonicalJson);
    }

    [Fact]
    public async Task SkillReadFailsClosedWhenApprovalHistoryIsInternallyInconsistent()
    {
        using SqlitePersistenceTestContext context =
            await SqlitePersistenceTestContext.CreateAsync();
        await context.SeedAuthorityAndSkillAsync();
        await using (SqliteConnection connection = await context.OpenRawAsync())
        {
            await ExecuteAsync(
                connection,
                "UPDATE skill_versions SET approved_utc = NULL " +
                "WHERE lifecycle_state = 'approved';");
        }

        StateReadResult<SkillVersionStateRecord> loaded =
            await context.StateStore.LoadSkillVersionAsync(
                SqlitePersistenceTestContext.SkillId,
                new SkillVersionNumber(1));

        Assert.False(loaded.IsSuccess);
        Assert.Equal("persistence.skill_version_corrupt", loaded.ReasonCode);
    }

    [Fact]
    public async Task SnapshotEpisodeAndPlanRejectMismatchedStoredAuthority()
    {
        using SqlitePersistenceTestContext context =
            await SqlitePersistenceTestContext.CreateAsync();
        await context.SeedAuthorityAndSkillAsync();
        StateWriteResult snapshot = await context.StateStore.StoreFolderSnapshotAsync(
            new FolderSnapshotStateRecord(
                Guid.Parse("789abcde-f012-4def-8012-789abcdef012"),
                SqlitePersistenceTestContext.GrantId,
                new ResourceRootIdentity("different-root"),
                SqlitePersistenceTestContext.Now.AddMinutes(2),
                SqlitePersistenceTestContext.Now.AddMinutes(2).AddSeconds(1),
                PersistedSnapshotStatus.Complete,
                ReasonCode: null,
                HashedBytes: 0,
                "{\"entries\":[]}",
                SqlitePersistenceTestContext.Now.AddHours(1)));

        CompanionId otherCompanion = new(
            Guid.Parse("89abcdef-0123-4efa-8123-89abcdef0123"));
        Assert.True(
            (await context.StateStore.StoreCompanionAsync(
                new CompanionStateRecord(
                    otherCompanion,
                    "Other companion",
                    SqlitePersistenceTestContext.Now,
                    1,
                    "{}"))).IsSuccess);
        TeachingEpisode wrongOwner = TeachingEpisode.Start(
            new TeachingEpisodeId(
                Guid.Parse("9abcdef0-1234-4fab-8234-9abcdef01234")),
            otherCompanion,
            SqlitePersistenceTestContext.GrantId,
            SqlitePersistenceTestContext.Now.AddMinutes(1));
        StateWriteResult episode = await context.StateStore.StoreTeachingEpisodeAsync(
            Episode(wrongOwner));

        ExecutionPlan valid = context.CreateStandardPlan(
            Guid.Parse("abcdef01-2345-4abc-8345-abcdef012345"));
        ExecutionPlanDefinition wrongRootDefinition = new(
            valid.Definition.Id,
            valid.Definition.SkillId,
            valid.Definition.SkillVersion,
            valid.Definition.SkillSpecificationHash,
            valid.Definition.GrantId,
            new ResourceRootIdentity("different-root"),
            valid.Definition.GrantedCapabilities,
            valid.Definition.CreatedUtc,
            valid.Definition.ExpiresUtc,
            valid.Definition.Operations);
        ExecutionPlan wrongRoot = CanonicalExecutionPlan.Create(
            wrongRootDefinition).Value!;
        StateWriteResult plan = await context.StateStore.StoreExecutionPlanAsync(
            wrongRoot,
            SqlitePersistenceTestContext.CanonicalJson(wrongRoot));

        Assert.False(snapshot.IsSuccess);
        Assert.Equal("persistence.grant_binding_mismatch", snapshot.FailureCode);
        Assert.False(episode.IsSuccess);
        Assert.Equal("persistence.grant_binding_mismatch", episode.FailureCode);
        Assert.False(plan.IsSuccess);
        Assert.Equal("persistence.plan_authority_mismatch", plan.FailureCode);
    }

    [Fact]
    public async Task TamperedGrantInvalidatesPlanReadsApprovalsAndJournalOpening()
    {
        using SqlitePersistenceTestContext context =
            await SqlitePersistenceTestContext.CreateAsync();
        await context.SeedAuthorityAndSkillAsync();
        ExecutionPlan plan = context.CreateStandardPlan(
            Guid.Parse("bcdef012-3456-4bcd-8456-bcdef0123456"));
        Assert.True(
            (await context.StateStore.StoreExecutionPlanAsync(
                plan,
                SqlitePersistenceTestContext.CanonicalJson(plan))).IsSuccess);
        PlanApproval firstApproval = PlanApproval.Issue(
            new ApprovalId(Guid.Parse("cdef0123-4567-4cde-8567-cdef01234567")),
            plan,
            SqlitePersistenceTestContext.Now.AddMinutes(3),
            SqlitePersistenceTestContext.Now.AddMinutes(30));
        Assert.True(
            (await context.StateStore.StoreApprovalAsync(firstApproval)).IsSuccess);
        PlanApproval consumed = firstApproval.Consume(
            plan,
            SqlitePersistenceTestContext.Now.AddMinutes(4)).Value!;
        ExecutionJournal originalJournal = ExecutionJournal.Open(
            new ExecutionId(Guid.Parse("ef012345-6789-4efa-8789-ef0123456789")),
            plan,
            SqlitePersistenceTestContext.Now.AddMinutes(4));
        JournalWriteResult initiallyOpened = await context.JournalStore.CreateAsync(
            originalJournal,
            consumed);
        Assert.True(initiallyOpened.IsSuccess, initiallyOpened.FailureCode);
        await using (SqliteConnection connection = await context.OpenRawAsync())
        {
            await ExecuteAsync(
                connection,
                "UPDATE resource_grants SET root_identity = 'tampered-root' " +
                "WHERE grant_id = '22222222-2222-4222-8222-222222222222';");
        }

        StateReadResult<StoredPlanDocument> loaded =
            await context.StateStore.LoadPlanDocumentAsync(plan.Definition.Id);
        PlanApproval secondApproval = PlanApproval.Issue(
            new ApprovalId(Guid.Parse("def01234-5678-4def-8678-def012345678")),
            plan,
            SqlitePersistenceTestContext.Now.AddMinutes(3),
            SqlitePersistenceTestContext.Now.AddMinutes(30));
        StateWriteResult storedSecond = await context.StateStore.StoreApprovalAsync(
            secondApproval);
        ExecutionJournalReadResult loadedJournal =
            await context.JournalStore.LoadJournalAsync(originalJournal.ExecutionId);
        JournalWriteResult appended = await context.JournalStore.AppendAsync(
            new StepIntentRecordedEvent(
                originalJournal.ExecutionId,
                2,
                SqlitePersistenceTestContext.Now.AddMinutes(5),
                1,
                FilePrimitive.EnsureDirectory,
                plan.Fingerprint,
                JournalInverseKind.RemoveCreatedEntry));
        ExecutionJournal secondJournal = ExecutionJournal.Open(
            new ExecutionId(Guid.Parse("f0123456-789a-4fab-889a-f0123456789a")),
            plan,
            SqlitePersistenceTestContext.Now.AddMinutes(4));
        JournalWriteResult reopened = await context.JournalStore.CreateAsync(
            secondJournal,
            consumed);

        Assert.False(loaded.IsSuccess);
        Assert.Equal("persistence.plan_corrupt", loaded.ReasonCode);
        Assert.False(storedSecond.IsSuccess);
        Assert.Equal("persistence.approval_plan_mismatch", storedSecond.FailureCode);
        Assert.False(loadedJournal.IsSuccess);
        Assert.Equal("persistence.journal_plan_invalid", loadedJournal.ReasonCode);
        Assert.False(appended.IsSuccess);
        Assert.Equal("persistence.journal_plan_invalid", appended.FailureCode);
        Assert.False(reopened.IsSuccess);
        Assert.Equal("persistence.execution_plan_invalid", reopened.FailureCode);
    }

    private static async Task StoreSnapshotAsync(
        SqlitePersistenceTestContext context,
        Guid snapshotId,
        int minute)
    {
        StateWriteResult stored = await context.StateStore.StoreFolderSnapshotAsync(
            new FolderSnapshotStateRecord(
                snapshotId,
                SqlitePersistenceTestContext.GrantId,
                context.Grant.RootIdentity,
                SqlitePersistenceTestContext.Now.AddMinutes(minute),
                SqlitePersistenceTestContext.Now.AddMinutes(minute).AddSeconds(1),
                PersistedSnapshotStatus.Complete,
                ReasonCode: null,
                HashedBytes: 0,
                "{\"entries\":[]}",
                SqlitePersistenceTestContext.Now.AddHours(1)));
        Assert.True(stored.IsSuccess, stored.FailureCode);
    }

    private static TeachingEpisodeStateRecord Episode(
        TeachingEpisode episode,
        Guid? baselineId = null,
        Guid? finalId = null,
        IReadOnlyList<DemonstrationExampleStateRecord>? examples = null) =>
        new(
            episode,
            baselineId,
            finalId,
            episode.State == TeachingEpisodeState.Reconciled
                ? "{\"effectCount\":1}"
                : null,
            SqlitePersistenceTestContext.Now.AddHours(2),
            examples ?? []);

    private static async Task AssertStoredAsync(
        SqlitePersistenceTestContext context,
        TeachingEpisodeStateRecord episode)
    {
        StateWriteResult stored = await context.StateStore.StoreTeachingEpisodeAsync(episode);
        Assert.True(stored.IsSuccess, stored.FailureCode);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(
        SqliteConnection connection,
        string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(
        SqliteConnection connection,
        string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture)!;
    }
}
