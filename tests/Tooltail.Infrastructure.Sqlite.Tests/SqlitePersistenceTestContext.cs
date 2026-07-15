using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Tooltail.Application.Abstractions;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Execution;
using Tooltail.Testing;

namespace Tooltail.Infrastructure.Sqlite.Tests;

internal sealed class SqlitePersistenceTestContext : IDisposable
{
    public static readonly DateTimeOffset Now =
        new(2026, 7, 16, 8, 0, 0, TimeSpan.Zero);

    public static readonly CompanionId CompanionId = new(
        Guid.Parse("11111111-1111-4111-8111-111111111111"));

    public static readonly GrantId GrantId = new(
        Guid.Parse("22222222-2222-4222-8222-222222222222"));

    public static readonly SkillId SkillId = new(
        Guid.Parse("33333333-3333-4333-8333-333333333333"));

    private readonly TemporaryDirectory temporary = new();

    private SqlitePersistenceTestContext()
    {
        DatabasePath = Path.Combine(temporary.Path, "state", "tooltail.db");
        Database = CreateDatabase();
        StateStore = new SqliteFileSkillStateStore(Database);
        JournalStore = new SqliteExecutionJournalStore(Database);
    }

    public string DatabasePath { get; }

    public TooltailSqliteDatabase Database { get; private set; }

    public SqliteFileSkillStateStore StateStore { get; private set; }

    public SqliteExecutionJournalStore JournalStore { get; private set; }

    public LocalFolderGrant Grant { get; private set; } = null!;

    public SkillVersion SkillVersion { get; private set; } = null!;

    public string SkillJson { get; } = "{}";

    public string SkillHash => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(SkillJson)));

    public static async Task<SqlitePersistenceTestContext> CreateAsync()
    {
        SqlitePersistenceTestContext context = new();
        SqliteDatabaseInitialization initialized = await context.Database.InitializeAsync();
        Assert.True(initialized.IsReady, initialized.ReasonCode);
        return context;
    }

    public async Task SeedAuthorityAndSkillAsync(
        SkillLifecycleState lifecycle = SkillLifecycleState.Approved)
    {
        StateWriteResult companion = await StateStore.StoreCompanionAsync(
            new CompanionStateRecord(
                CompanionId,
                "Test companion",
                Now,
                1,
                "{}"));
        Assert.True(companion.IsSuccess, companion.FailureCode);

        Grant = LocalFolderGrant.Issue(
            GrantId,
            CompanionId,
            new ResourceRootIdentity("test-root-identity"),
            [
                GrantCapability.Enumerate,
                GrantCapability.ReadMetadata,
                GrantCapability.ReadContentHash,
                GrantCapability.CreateDirectory,
                GrantCapability.Rename,
                GrantCapability.MoveWithinRoot,
                GrantCapability.CopyWithinRoot,
            ],
            Now,
            Now.AddDays(1));
        StateWriteResult grant = await StateStore.StoreLocalFolderGrantAsync(
            new LocalFolderGrantStateRecord(Grant, [1, 2, 3, 4]));
        Assert.True(grant.IsSuccess, grant.FailureCode);

        SkillVersion draft = new(
            SkillId,
            new SkillVersionNumber(1),
            parent: null,
            SkillHash,
            "0.1.0-test",
            "0.1.0-test",
            SkillLifecycleState.Draft,
            Now);
        StateWriteResult storedDraft = await StateStore.StoreSkillVersionAsync(
            SkillRecord(draft, approvedUtc: null));
        Assert.True(storedDraft.IsSuccess, storedDraft.FailureCode);

        SkillVersion current = draft;
        DateTimeOffset? approvedUtc = null;
        foreach (SkillLifecycleState transition in TransitionsTo(lifecycle))
        {
            current = current.TransitionTo(transition).Value!;
            if (transition == SkillLifecycleState.Approved)
            {
                approvedUtc = Now.AddMinutes(1);
            }

            StateWriteResult stored = await StateStore.StoreSkillVersionAsync(
                SkillRecord(current, approvedUtc));
            Assert.True(stored.IsSuccess, stored.FailureCode);
        }

        SkillVersion = current;
    }

    public SkillVersionStateRecord SkillRecord(
        SkillVersion version,
        DateTimeOffset? approvedUtc) =>
        new(
            CompanionId,
            "Organize test files",
            Now,
            version,
            MakeCurrent: true,
            "tooltail.skill-spec/1",
            SkillJson,
            "test.compiler",
            approvedUtc,
            SemanticDiffJson: null);

    public ExecutionPlan CreateStandardPlan(
        Guid planId,
        string destination = "sorted",
        DateTimeOffset? createdUtc = null)
    {
        DateTimeOffset created = createdUtc ?? Now.AddMinutes(2);
        ExecutionPlanDefinition definition = new(
            new PlanId(planId),
            SkillId,
            new SkillVersionNumber(1),
            new SkillSpecificationHash(SkillHash),
            GrantId,
            Grant.RootIdentity,
            Grant.Capabilities,
            created,
            created.AddHours(1),
            [
                new PlannedFileOperation(
                    1,
                    FilePrimitive.EnsureDirectory,
                    sourceRelativePath: null,
                    destination,
                    sourceFingerprint: null,
                    DestinationPrecondition.Absent,
                    ExpectedSourceState.NotApplicable,
                    ExpectedDestinationState.DirectoryPresent),
            ]);
        var result = CanonicalExecutionPlan.Create(definition);
        Assert.True(result.IsSuccess, result.Error?.Code);
        return result.Value!;
    }

    public RecoveryPlan CreateRecoveryPlan(
        Guid planId,
        ExecutionJournal originalJournal,
        VerifiedEntryEvidence expectedSource,
        DateTimeOffset createdUtc)
    {
        RecoveryPlanDefinition definition = new(
            new PlanId(planId),
            originalJournal.ExecutionId,
            originalJournal.PlanId,
            originalJournal.PlanFingerprint,
            SkillId,
            new SkillVersionNumber(1),
            new SkillSpecificationHash(SkillHash),
            GrantId,
            Grant.RootIdentity,
            Grant.Capabilities,
            createdUtc,
            createdUtc.AddHours(1),
            [
                new PlannedRecoveryOperation(
                    1,
                    1,
                    FilePrimitive.EnsureDirectory,
                    RecoveryPrimitive.RemoveCreatedEntry,
                    "sorted",
                    destinationRelativePath: null,
                    expectedSource,
                    originalDestinationWasAbsent: true),
            ]);
        var result = CanonicalRecoveryPlan.Create(definition);
        Assert.True(result.IsSuccess, result.Error?.Code);
        return result.Value!;
    }

    public static string CanonicalJson(ExecutionPlan plan) =>
        Encoding.UTF8.GetString(CanonicalExecutionPlan.Encode(plan.Definition));

    public static string CanonicalJson(RecoveryPlan plan) =>
        Encoding.UTF8.GetString(CanonicalRecoveryPlan.Encode(plan.Definition));

    public async Task RestartAsync()
    {
        Database = CreateDatabase();
        SqliteDatabaseInitialization initialized = await Database.InitializeAsync();
        Assert.True(initialized.IsReady, initialized.ReasonCode);
        StateStore = new SqliteFileSkillStateStore(Database);
        JournalStore = new SqliteExecutionJournalStore(Database);
    }

    public async Task<SqliteConnection> OpenRawAsync()
    {
        SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON;";
        _ = await command.ExecuteNonQueryAsync();
        return connection;
    }

    public void Dispose() => temporary.Dispose();

    private TooltailSqliteDatabase CreateDatabase() =>
        new(
            new SqliteDatabaseOptions(DatabasePath, "0.1.0-test"),
            new FixedClock(Now),
            new FixedIdGenerator());

    private static IEnumerable<SkillLifecycleState> TransitionsTo(
        SkillLifecycleState target) =>
        target switch
        {
            SkillLifecycleState.Draft => [],
            SkillLifecycleState.Approved => [SkillLifecycleState.Approved],
            SkillLifecycleState.Practiced =>
                [SkillLifecycleState.Approved, SkillLifecycleState.Practiced],
            SkillLifecycleState.Reliable =>
                [
                    SkillLifecycleState.Approved,
                    SkillLifecycleState.Practiced,
                    SkillLifecycleState.Reliable,
                ],
            SkillLifecycleState.Delegated =>
                [
                    SkillLifecycleState.Approved,
                    SkillLifecycleState.Practiced,
                    SkillLifecycleState.Reliable,
                    SkillLifecycleState.Delegated,
                ],
            SkillLifecycleState.Stale => [SkillLifecycleState.Stale],
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FixedIdGenerator : IIdGenerator
    {
        public Guid NewId() =>
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    }
}
