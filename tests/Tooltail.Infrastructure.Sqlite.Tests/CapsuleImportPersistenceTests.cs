using Tooltail.Application.Abstractions;
using Tooltail.Contracts.Capsules;
using Tooltail.Contracts.Json;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Continuity;

namespace Tooltail.Infrastructure.Sqlite.Tests;

public sealed class CapsuleImportPersistenceTests
{
    [Fact]
    public async Task PristineCompanionIsAtomicallyReplacedByStaleAuthorityFreeCapsule()
    {
        using SqlitePersistenceTestContext context =
            await SqlitePersistenceTestContext.CreateAsync();
        CompanionId emptyCompanion = SqlitePersistenceTestContext.CompanionId;
        await StoreEmptyCompanionAsync(context, emptyCompanion);
        byte[] capsule = ReadExampleBytes();
        CompanionCapsuleContract parsed =
            ContractJson.ParseCompanionCapsule(capsule).Value!;
        CompanionCapsuleImportService service = new(context.StateStore);

        CapsuleImportResult imported = await service.ImportAsync(
            capsule,
            emptyCompanion,
            TestContext.Current.CancellationToken);
        await context.RestartAsync();
        StateReadResult<IReadOnlyList<CompanionStateRecord>> companions =
            await context.StateStore.ListCompanionsAsync(
                TestContext.Current.CancellationToken);
        CompanionId importedId = new(parsed.Companion.CompanionId);
        StateReadResult<FileSkillWorkspaceStateRecord> workspace =
            await context.StateStore.LoadWorkspaceStateAsync(
                importedId,
                TestContext.Current.CancellationToken);

        Assert.True(imported.IsSuccess, imported.ReasonCode);
        Assert.Equal("capsule.imported_unbound", imported.ReasonCode);
        Assert.Equal(importedId, imported.ImportedCompanionId);
        Assert.Equal(parsed.Skills.Count, imported.ImportedSkillVersionCount);
        Assert.True(companions.IsSuccess, companions.ReasonCode);
        Assert.Collection(
            companions.Value!,
            companion => Assert.Equal(importedId, companion.Id));
        Assert.True(workspace.IsSuccess, workspace.ReasonCode);
        Assert.Empty(workspace.Value!.Grants);
        Assert.Empty(workspace.Value.Executions);
        Assert.All(
            workspace.Value.CurrentSkills,
            skill =>
            {
                Assert.Equal(SkillLifecycleState.Stale, skill.Version.Lifecycle);
                Assert.Null(skill.ApprovedUtc);
            });
    }

    [Fact]
    public async Task ExistingAuthorityRejectsImportWithoutChangingOriginalCompanion()
    {
        using SqlitePersistenceTestContext context =
            await SqlitePersistenceTestContext.CreateAsync();
        await context.SeedAuthorityAndSkillAsync();
        CompanionCapsuleImportService service = new(context.StateStore);

        CapsuleImportResult imported = await service.ImportAsync(
            ReadExampleBytes(),
            SqlitePersistenceTestContext.CompanionId,
            TestContext.Current.CancellationToken);
        StateReadResult<IReadOnlyList<CompanionStateRecord>> companions =
            await context.StateStore.ListCompanionsAsync(
                TestContext.Current.CancellationToken);
        StateReadResult<FileSkillWorkspaceStateRecord> workspace =
            await context.StateStore.LoadWorkspaceStateAsync(
                SqlitePersistenceTestContext.CompanionId,
                TestContext.Current.CancellationToken);

        Assert.False(imported.IsSuccess);
        Assert.Equal(
            "persistence.capsule_import_state_not_pristine",
            imported.ReasonCode);
        Assert.True(companions.IsSuccess, companions.ReasonCode);
        Assert.Collection(
            companions.Value!,
            companion => Assert.Equal(
                SqlitePersistenceTestContext.CompanionId,
                companion.Id));
        Assert.True(workspace.IsSuccess, workspace.ReasonCode);
        Assert.NotEmpty(workspace.Value!.Grants);
        Assert.NotEmpty(workspace.Value.CurrentSkills);
    }

    [Fact]
    public async Task NewExactGrantRebindsImportedStaleParentToScopeOnlyDraft()
    {
        using SqlitePersistenceTestContext context =
            await SqlitePersistenceTestContext.CreateAsync();
        CompanionId emptyCompanion = SqlitePersistenceTestContext.CompanionId;
        await StoreEmptyCompanionAsync(context, emptyCompanion);
        CompanionCapsuleImportService importer = new(context.StateStore);
        CapsuleImportResult imported = await importer.ImportAsync(
            ReadExampleBytes(),
            emptyCompanion,
            TestContext.Current.CancellationToken);
        Assert.True(imported.IsSuccess, imported.ReasonCode);
        CompanionId importedId = imported.ImportedCompanionId!.Value;
        GrantId grantId = new(
            Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee"));
        LocalFolderGrant grant = LocalFolderGrant.Issue(
            grantId,
            importedId,
            new ResourceRootIdentity("capsule-rebind-test-root"),
            [
                GrantCapability.Enumerate,
                GrantCapability.ReadMetadata,
                GrantCapability.ReadContentHash,
                GrantCapability.CreateDirectory,
                GrantCapability.Rename,
                GrantCapability.MoveWithinRoot,
                GrantCapability.CopyWithinRoot,
            ],
            SqlitePersistenceTestContext.Now,
            SqlitePersistenceTestContext.Now.AddDays(1));
        StateWriteResult storedGrant = await context.StateStore.StoreLocalFolderGrantAsync(
            new LocalFolderGrantStateRecord(grant, ProtectedCanonicalRoot: null),
            TestContext.Current.CancellationToken);
        Assert.True(storedGrant.IsSuccess, storedGrant.FailureCode);
        CompanionCapsuleRebindPersistenceService rebind = new(
            context.StateStore,
            new FixedClock(SqlitePersistenceTestContext.Now));

        CapsulePersistedRebindResult rebound = await rebind.RebindNextAsync(
            importedId,
            grant,
            TestContext.Current.CancellationToken);
        Assert.True(rebound.IsSuccess, rebound.ReasonCode);
        Assert.Equal("capsule.rebind_draft_persisted", rebound.ReasonCode);
        await context.RestartAsync();
        StateReadResult<FileSkillWorkspaceStateRecord> workspace =
            await context.StateStore.LoadWorkspaceStateAsync(
                importedId,
                TestContext.Current.CancellationToken);
        StateReadResult<IReadOnlyList<SkillVersionStateRecord>> history =
            await context.StateStore.LoadSkillVersionsAsync(
                rebound.ReboundSkillId!.Value,
                TestContext.Current.CancellationToken);

        Assert.Equal(grantId.Value, rebound.Rebound!.Applicability.RootGrantId);
        Assert.Equal(["scope_binding"],
            Tooltail.Features.FileSkills.Correction.SkillCorrectionService.Compare(
                rebound.Parent!,
                rebound.Rebound).ChangedFields);
        Assert.True(workspace.IsSuccess, workspace.ReasonCode);
        Assert.Single(workspace.Value!.Grants);
        Assert.Collection(
            workspace.Value.CurrentSkills,
            current => Assert.Equal(
                SkillLifecycleState.Draft,
                current.Version.Lifecycle));
        Assert.True(history.IsSuccess, history.ReasonCode);
        Assert.Collection(
            history.Value!,
            parent =>
            {
                Assert.Equal(SkillLifecycleState.Stale, parent.Version.Lifecycle);
                Assert.Null(parent.ApprovedUtc);
            },
            current =>
            {
                Assert.Equal(SkillLifecycleState.Draft, current.Version.Lifecycle);
                Assert.Null(current.ApprovedUtc);
            });
        Assert.Empty(workspace.Value.Executions);
    }

    [Fact]
    public async Task PreCancelledImportLeavesPristineCompanionUntouched()
    {
        using SqlitePersistenceTestContext context =
            await SqlitePersistenceTestContext.CreateAsync();
        CompanionId emptyCompanion = SqlitePersistenceTestContext.CompanionId;
        await StoreEmptyCompanionAsync(context, emptyCompanion);
        CompanionCapsuleImportService service = new(context.StateStore);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ImportAsync(
                ReadExampleBytes(),
                emptyCompanion,
                cancellation.Token));
        StateReadResult<IReadOnlyList<CompanionStateRecord>> companions =
            await context.StateStore.ListCompanionsAsync(
                TestContext.Current.CancellationToken);

        Assert.True(companions.IsSuccess, companions.ReasonCode);
        Assert.Collection(
            companions.Value!,
            companion => Assert.Equal(emptyCompanion, companion.Id));
    }

    private static async Task StoreEmptyCompanionAsync(
        SqlitePersistenceTestContext context,
        CompanionId companionId)
    {
        StateWriteResult stored = await context.StateStore.StoreCompanionAsync(
            new CompanionStateRecord(
                companionId,
                "Empty first-run companion",
                SqlitePersistenceTestContext.Now,
                1,
                "{}"),
            TestContext.Current.CancellationToken);
        Assert.True(stored.IsSuccess, stored.FailureCode);
    }

    private static byte[] ReadExampleBytes()
    {
        for (DirectoryInfo? current = new(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            string candidate = Path.Combine(
                current.FullName,
                "docs",
                "examples",
                "companion-capsule.example.json");
            if (File.Exists(candidate))
            {
                return File.ReadAllBytes(candidate);
            }
        }

        throw new DirectoryNotFoundException("Could not locate capsule example.");
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
