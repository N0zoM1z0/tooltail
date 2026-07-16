using Tooltail.Application.Abstractions;
using Tooltail.Infrastructure.Sqlite;
using Tooltail.Testing;

namespace Tooltail.Infrastructure.Sqlite.Tests;

public sealed class LocalStateDeletionServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 16, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CleanFirstLaunchWithoutApplicationDirectoriesNeedsNoRecovery()
    {
        using TemporaryDirectory temporary = new();
        string applicationRoot = Path.Combine(temporary.Path, "not-created");
        TooltailSqliteDatabase database = new(
            new SqliteDatabaseOptions(
                Path.Combine(applicationRoot, "state", "tooltail.db"),
                "test"),
            new FixedClock(Now),
            new SequenceIds());
        LocalStateDeletionService service = new(
            database,
            new FixedClock(Now),
            new SequenceIds());

        LocalStateDeletionResult result = service.RecoverPendingDeletion();

        Assert.True(result.IsSuccess, result.ReasonCode);
        Assert.False(result.RequiresShutdown);
        Assert.False(result.RequiresRecovery);
        Assert.False(Directory.Exists(applicationRoot));
    }

    [Fact]
    public async Task ExactAuthorizationDeletesOnlyDatabaseStateAndPreservesLabsAndExports()
    {
        using DeletionFixture fixture = await DeletionFixture.CreateAsync();
        LocalStateDeletionPreview preview = fixture.Service.Prepare();

        LocalStateDeletionResult wrong = await fixture.Service.DeleteAsync(Guid.NewGuid());
        Assert.False(wrong.IsSuccess);
        Assert.True(File.Exists(fixture.DatabasePath));

        LocalStateDeletionResult result = await fixture.Service.DeleteAsync(
            preview.RequestId!.Value,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.ReasonCode);
        Assert.True(result.RequiresShutdown);
        Assert.False(result.RequiresRecovery);
        Assert.False(File.Exists(fixture.DatabasePath));
        Assert.False(File.Exists($"{fixture.DatabasePath}-wal"));
        Assert.False(File.Exists($"{fixture.DatabasePath}-shm"));
        Assert.False(File.Exists(fixture.IntentPath));
        Assert.Equal("keep lab", File.ReadAllText(fixture.LabSentinel));
        Assert.Equal("keep capsule", File.ReadAllText(fixture.CapsuleSentinel));
        Assert.Equal("keep diagnostic", File.ReadAllText(fixture.DiagnosticSentinel));
        Assert.Equal("keep unrelated", File.ReadAllText(fixture.UnrelatedStateFile));
    }

    [Theory]
    [InlineData(LocalStateDeletionBoundary.IntentPersisted)]
    [InlineData(LocalStateDeletionBoundary.DatabaseRemoved)]
    [InlineData(LocalStateDeletionBoundary.SidecarsRemoved)]
    public async Task StartupRecoveryCompletesEveryIncompleteDeletionPrefix(
        LocalStateDeletionBoundary boundary)
    {
        using DeletionFixture fixture = await DeletionFixture.CreateAsync();
        LocalStateDeletionPreview preview = fixture.Service.Prepare();
        ThrowingInjector injector = new(boundary);

        await Assert.ThrowsAsync<InjectedDeletionCrash>(
            () => fixture.Service.DeleteAsync(
                preview.RequestId!.Value,
                injector,
                TestContext.Current.CancellationToken));

        Assert.True(File.Exists(fixture.IntentPath));
        LocalStateDeletionResult recovered = fixture.Service.RecoverPendingDeletion();
        Assert.True(recovered.IsSuccess, recovered.ReasonCode);
        Assert.False(recovered.RequiresShutdown);
        Assert.False(File.Exists(fixture.DatabasePath));
        Assert.False(File.Exists($"{fixture.DatabasePath}-wal"));
        Assert.False(File.Exists($"{fixture.DatabasePath}-shm"));
        Assert.False(File.Exists(fixture.IntentPath));
        Assert.True(File.Exists(fixture.LabSentinel));
        Assert.True(File.Exists(fixture.CapsuleSentinel));
        Assert.True(File.Exists(fixture.DiagnosticSentinel));
    }

    [Fact]
    public async Task InvalidIntentFailsClosedWithoutDeletingDatabase()
    {
        using DeletionFixture fixture = await DeletionFixture.CreateAsync();
        await File.WriteAllTextAsync(
            fixture.IntentPath,
            "{\"schemaVersion\":\"unknown\"}",
            TestContext.Current.CancellationToken);

        LocalStateDeletionResult result = fixture.Service.RecoverPendingDeletion();

        Assert.False(result.IsSuccess);
        Assert.True(result.RequiresRecovery);
        Assert.True(result.RequiresShutdown);
        Assert.True(File.Exists(fixture.DatabasePath));
        Assert.True(File.Exists(fixture.IntentPath));
    }

    [Fact]
    public async Task OversizedIntentFailsClosedWithoutReadingOrDeletingDatabase()
    {
        using DeletionFixture fixture = await DeletionFixture.CreateAsync();
        await File.WriteAllBytesAsync(
            fixture.IntentPath,
            new byte[4097],
            TestContext.Current.CancellationToken);

        LocalStateDeletionResult result = fixture.Service.RecoverPendingDeletion();

        Assert.False(result.IsSuccess);
        Assert.Equal("local_state.delete_recovery_failed", result.ReasonCode);
        Assert.True(File.Exists(fixture.DatabasePath));
        Assert.True(File.Exists(fixture.IntentPath));
    }

    [Fact]
    public async Task WrongRootFingerprintFailsClosedWithoutDeletingDatabase()
    {
        using DeletionFixture fixture = await DeletionFixture.CreateAsync();
        await File.WriteAllTextAsync(
            fixture.IntentPath,
            """
            {"schemaVersion":"tooltail.local-state-deletion/1","requestId":"24703684-0aba-41ff-9de0-cc403bc91d8c","requestedUtc":"2026-07-16T15:00:00+00:00","applicationRootFingerprint":"0000000000000000000000000000000000000000000000000000000000000000"}
            """,
            TestContext.Current.CancellationToken);

        LocalStateDeletionResult result = fixture.Service.RecoverPendingDeletion();

        Assert.False(result.IsSuccess);
        Assert.True(File.Exists(fixture.DatabasePath));
        Assert.True(File.Exists(fixture.IntentPath));
    }

    [NonWindowsFact]
    public async Task LinkedApplicationRootCannotIssueDeletionAuthorization()
    {
        using TemporaryDirectory temporary = new();
        string actualRoot = Path.Combine(temporary.Path, "actual");
        string actualState = Path.Combine(actualRoot, "state");
        Directory.CreateDirectory(actualState);
        string actualDatabase = Path.Combine(actualState, "tooltail.db");
        await File.WriteAllTextAsync(
            actualDatabase,
            "preserve",
            TestContext.Current.CancellationToken);
        string linkedRoot = Path.Combine(temporary.Path, "linked");
        Directory.CreateSymbolicLink(linkedRoot, actualRoot);
        TooltailSqliteDatabase database = new(
            new SqliteDatabaseOptions(
                Path.Combine(linkedRoot, "state", "tooltail.db"),
                "test"),
            new FixedClock(Now),
            new SequenceIds());
        LocalStateDeletionService service = new(
            database,
            new FixedClock(Now),
            new SequenceIds());

        LocalStateDeletionPreview preview = service.Prepare();

        Assert.False(preview.IsSuccess);
        Assert.Equal("local_state.delete_unavailable", preview.ReasonCode);
        Assert.Equal("preserve", await File.ReadAllTextAsync(
            actualDatabase,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExpiredAuthorizationCannotCreateDeletionIntent()
    {
        using TemporaryDirectory temporary = new();
        MutableClock clock = new(Now);
        DeletionFixture fixture = await DeletionFixture.CreateAsync(temporary, clock);
        LocalStateDeletionPreview preview = fixture.Service.Prepare();
        clock.UtcNow = Now.AddMinutes(5);

        LocalStateDeletionResult result = await fixture.Service.DeleteAsync(
            preview.RequestId!.Value,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("local_state.delete_authorization_expired", result.ReasonCode);
        Assert.True(File.Exists(fixture.DatabasePath));
        Assert.False(File.Exists(fixture.IntentPath));
    }

    [Fact]
    public async Task CancellationBeforeIntentLeavesDatabaseAndNoRecoveryMarker()
    {
        using DeletionFixture fixture = await DeletionFixture.CreateAsync();
        LocalStateDeletionPreview preview = fixture.Service.Prepare();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => fixture.Service.DeleteAsync(
                preview.RequestId!.Value,
                cancellationToken: cancellation.Token));

        Assert.True(File.Exists(fixture.DatabasePath));
        Assert.False(File.Exists(fixture.IntentPath));
    }

    [Fact]
    public async Task CrashAfterIntentRemovalIsAlreadyACompleteDeletion()
    {
        using DeletionFixture fixture = await DeletionFixture.CreateAsync();
        LocalStateDeletionPreview preview = fixture.Service.Prepare();

        await Assert.ThrowsAsync<InjectedDeletionCrash>(
            () => fixture.Service.DeleteAsync(
                preview.RequestId!.Value,
                new ThrowingInjector(LocalStateDeletionBoundary.IntentRemoved),
                TestContext.Current.CancellationToken));

        Assert.False(File.Exists(fixture.DatabasePath));
        Assert.False(File.Exists(fixture.IntentPath));
        LocalStateDeletionResult startup = fixture.Service.RecoverPendingDeletion();
        Assert.True(startup.IsSuccess, startup.ReasonCode);
        Assert.False(startup.RequiresRecovery);
        Assert.False(startup.RequiresShutdown);
    }

    [Fact]
    public async Task DirectorySubstitutedIntoDatabaseSlotFailsClosedAndKeepsIntent()
    {
        using DeletionFixture fixture = await DeletionFixture.CreateAsync();
        LocalStateDeletionPreview preview = fixture.Service.Prepare();
        File.Delete(fixture.DatabasePath);
        Directory.CreateDirectory(fixture.DatabasePath);

        LocalStateDeletionResult result = await fixture.Service.DeleteAsync(
            preview.RequestId!.Value,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.True(result.RequiresRecovery);
        Assert.True(result.RequiresShutdown);
        Assert.True(Directory.Exists(fixture.DatabasePath));
        Assert.True(File.Exists(fixture.IntentPath));
    }

    private sealed class DeletionFixture : IDisposable
    {
        private readonly TemporaryDirectory temporary;

        private DeletionFixture(
            TemporaryDirectory temporary,
            string databasePath,
            LocalStateDeletionService service,
            string labSentinel,
            string capsuleSentinel,
            string diagnosticSentinel,
            string unrelatedStateFile)
        {
            this.temporary = temporary;
            DatabasePath = databasePath;
            Service = service;
            LabSentinel = labSentinel;
            CapsuleSentinel = capsuleSentinel;
            DiagnosticSentinel = diagnosticSentinel;
            UnrelatedStateFile = unrelatedStateFile;
        }

        public string DatabasePath { get; }

        public string IntentPath => Path.Combine(
            Path.GetDirectoryName(DatabasePath)!,
            "local-state-deletion.intent.json");

        public LocalStateDeletionService Service { get; }

        public string LabSentinel { get; }

        public string CapsuleSentinel { get; }

        public string DiagnosticSentinel { get; }

        public string UnrelatedStateFile { get; }

        public static async Task<DeletionFixture> CreateAsync()
        {
            TemporaryDirectory temporary = new();
            return await CreateAsync(temporary, new FixedClock(Now));
        }

        public static async Task<DeletionFixture> CreateAsync(
            TemporaryDirectory temporary,
            IClock clock)
        {
            string stateRoot = Path.Combine(temporary.Path, "state");
            string labRoot = Path.Combine(temporary.Path, "Labs", "preserved");
            string exportRoot = Path.Combine(temporary.Path, "Exports");
            string diagnosticRoot = Path.Combine(temporary.Path, "Diagnostics");
            Directory.CreateDirectory(stateRoot);
            Directory.CreateDirectory(labRoot);
            Directory.CreateDirectory(exportRoot);
            Directory.CreateDirectory(diagnosticRoot);
            string databasePath = Path.Combine(stateRoot, "tooltail.db");
            TooltailSqliteDatabase database = new(
                new SqliteDatabaseOptions(databasePath, "test"),
                clock,
                new SequenceIds());
            SqliteDatabaseInitialization initialized = await database.InitializeAsync(
                TestContext.Current.CancellationToken);
            Assert.True(initialized.IsReady, initialized.ReasonCode);
            await File.WriteAllTextAsync(
                $"{databasePath}-wal",
                "synthetic wal",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                $"{databasePath}-shm",
                "synthetic shm",
                TestContext.Current.CancellationToken);
            string lab = Path.Combine(labRoot, "keep.txt");
            string capsule = Path.Combine(exportRoot, "keep.capsule.json");
            string diagnostic = Path.Combine(
                diagnosticRoot,
                "keep.tooltail-diagnostic.json");
            string unrelated = Path.Combine(stateRoot, "unrelated.keep");
            await File.WriteAllTextAsync(lab, "keep lab", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                capsule,
                "keep capsule",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                diagnostic,
                "keep diagnostic",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                unrelated,
                "keep unrelated",
                TestContext.Current.CancellationToken);
            LocalStateDeletionService service = new(
                database,
                clock,
                new SequenceIds());
            return new DeletionFixture(
                temporary,
                databasePath,
                service,
                lab,
                capsule,
                diagnostic,
                unrelated);
        }

        public void Dispose() => temporary.Dispose();
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class SequenceIds : IIdGenerator
    {
        private long next;

        public Guid NewId()
        {
            Span<byte> bytes = stackalloc byte[16];
            BitConverter.TryWriteBytes(bytes[8..], Interlocked.Increment(ref next));
            bytes[7] = 0x40;
            return new Guid(bytes);
        }
    }

    private sealed class ThrowingInjector(
        LocalStateDeletionBoundary target) : ILocalStateDeletionFaultInjector
    {
        public void Reach(LocalStateDeletionBoundary boundary)
        {
            if (boundary == target)
            {
                throw new InjectedDeletionCrash();
            }
        }
    }

    private sealed class InjectedDeletionCrash : Exception;

    private sealed class NonWindowsFactAttribute : FactAttribute
    {
        public NonWindowsFactAttribute(
            [System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
            [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
            : base(sourceFilePath, sourceLineNumber)
        {
            if (OperatingSystem.IsWindows())
            {
                Skip = "Portable symlink fixture runs on non-Windows; the Windows apphost exercises the fixed local layout.";
            }
        }
    }
}
