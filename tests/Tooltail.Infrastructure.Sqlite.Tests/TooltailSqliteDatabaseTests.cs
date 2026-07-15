using Microsoft.Data.Sqlite;
using Tooltail.Application.Abstractions;
using Tooltail.Testing;

namespace Tooltail.Infrastructure.Sqlite.Tests;

public sealed class TooltailSqliteDatabaseTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 16, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FreshDatabaseAppliesGoldenStrictSchemaAndDurabilityPragmas()
    {
        using TemporaryDirectory temporary = new();
        TooltailSqliteDatabase database = CreateDatabase(temporary);

        SqliteDatabaseInitialization initialized = await database.InitializeAsync();

        Assert.True(initialized.IsReady, initialized.ReasonCode);
        Assert.Equal(1, initialized.SchemaVersion);
        Assert.Equal([1], initialized.AppliedVersions);
        Assert.Equal("wal", initialized.JournalMode);
        Assert.False(initialized.BackupCreated);
        Assert.True(File.Exists(database.DatabasePath));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(database.DatabasePath) &
                    (UnixFileMode.UserRead |
                     UnixFileMode.UserWrite |
                     UnixFileMode.GroupRead |
                     UnixFileMode.GroupWrite |
                     UnixFileMode.OtherRead |
                     UnixFileMode.OtherWrite));
        }

        await using SqliteConnection connection = await database.OpenReadOnlyAsync();
        Assert.Equal(1L, await ScalarInt64Async(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(1L, await ScalarInt64Async(connection, "PRAGMA query_only;"));
        Assert.Equal("wal", await ScalarStringAsync(connection, "PRAGMA journal_mode;"));
        Assert.Equal("ok", await ScalarStringAsync(connection, "PRAGMA quick_check(1);"));
        Assert.Equal(
            "92560705c515c686a430c793167d10e286e92d2e14e2007800b4519216049288",
            await ScalarStringAsync(
                connection,
                "SELECT checksum FROM schema_migrations WHERE version = 1;"));

        HashSet<string> strictTables = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_list;";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                string name = reader.GetString(1);
                if (!name.StartsWith("sqlite_", StringComparison.Ordinal))
                {
                    Assert.Equal(1L, reader.GetInt64(5));
                    strictTables.Add(name);
                }
            }
        }

        Assert.Equal(ExpectedTables, strictTables);
    }

    [Fact]
    public async Task ConcurrentAndRepeatedInitializationAppliesEachMigrationOnce()
    {
        using TemporaryDirectory temporary = new();
        TooltailSqliteDatabase first = CreateDatabase(temporary);
        TooltailSqliteDatabase second = CreateDatabase(temporary);

        SqliteDatabaseInitialization[] initialized = await Task.WhenAll(
            first.InitializeAsync(),
            second.InitializeAsync());
        SqliteDatabaseInitialization repeated = await first.InitializeAsync();

        Assert.All(initialized, result => Assert.True(result.IsReady, result.ReasonCode));
        Assert.Equal(1, initialized.Sum(result => result.AppliedVersions.Count));
        Assert.True(repeated.IsReady, repeated.ReasonCode);
        Assert.Empty(repeated.AppliedVersions);
        await using SqliteConnection connection = await first.OpenReadOnlyAsync();
        Assert.Equal(
            1L,
            await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM schema_migrations;"));
    }

    [Fact]
    public async Task UnmanagedDatabaseIsPreservedAndNeverSilentlyInitialized()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "state", "tooltail.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (SqliteConnection connection = await OpenRawAsync(path))
        {
            await ExecuteAsync(
                connection,
                "CREATE TABLE existing_user_data (value TEXT NOT NULL);" +
                "INSERT INTO existing_user_data (value) VALUES ('preserve-me');");
        }

        TooltailSqliteDatabase database = CreateDatabase(path);
        SqliteDatabaseInitialization initialized = await database.InitializeAsync();

        Assert.Equal(
            SqliteDatabaseStatus.ReadOnlyRecoveryRequired,
            initialized.Status);
        Assert.Equal("sqlite.schema_unmanaged", initialized.ReasonCode);
        await using SqliteConnection readOnly = await database.OpenReadOnlyAsync();
        Assert.Equal(
            "preserve-me",
            await ScalarStringAsync(
                readOnly,
                "SELECT value FROM existing_user_data;"));
        Assert.Equal(
            0L,
            await ScalarInt64Async(
                readOnly,
                "SELECT COUNT(*) FROM sqlite_schema " +
                "WHERE type = 'table' AND name = 'schema_migrations';"));
    }

    [Fact]
    public async Task ChecksumMismatchEntersReadOnlyRecoveryWithoutChangingData()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "tooltail.db");
        await using (SqliteConnection connection = await OpenRawAsync(path))
        {
            await ExecuteAsync(
                connection,
                "CREATE TABLE schema_migrations (" +
                "version INTEGER PRIMARY KEY, " +
                "applied_utc TEXT NOT NULL, " +
                "application_version TEXT NOT NULL, " +
                "checksum TEXT NOT NULL) STRICT;" +
                "CREATE TABLE sentinel (value TEXT NOT NULL) STRICT;" +
                "INSERT INTO schema_migrations VALUES " +
                "(1, '2026-07-16T08:00:00.0000000Z', '0.1.0', " +
                "'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa');" +
                "INSERT INTO sentinel VALUES ('still-here');");
        }

        TooltailSqliteDatabase database = CreateDatabase(path);
        SqliteDatabaseInitialization initialized = await database.InitializeAsync();

        Assert.Equal("sqlite.migration_checksum_mismatch", initialized.ReasonCode);
        await using SqliteConnection readOnly = await database.OpenReadOnlyAsync();
        Assert.Equal(
            "still-here",
            await ScalarStringAsync(readOnly, "SELECT value FROM sentinel;"));
        Assert.Equal(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            await ScalarStringAsync(
                readOnly,
                "SELECT checksum FROM schema_migrations WHERE version = 1;"));
    }

    [Fact]
    public async Task UnknownFutureMigrationAndMissingSchemaObjectFailClosed()
    {
        using TemporaryDirectory futureTemporary = new();
        TooltailSqliteDatabase future = CreateDatabase(futureTemporary);
        Assert.True((await future.InitializeAsync()).IsReady);
        await using (SqliteConnection connection = await OpenRawAsync(future.DatabasePath))
        {
            await ExecuteAsync(
                connection,
                "INSERT INTO schema_migrations " +
                "(version, applied_utc, application_version, checksum) VALUES " +
                "(2, '2026-07-16T08:00:00.0000000Z', 'future', " +
                "'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb');");
        }

        SqliteDatabaseInitialization unknown = await future.InitializeAsync();
        Assert.Equal("sqlite.migration_unknown", unknown.ReasonCode);

        using TemporaryDirectory incompleteTemporary = new();
        TooltailSqliteDatabase incomplete = CreateDatabase(incompleteTemporary);
        Assert.True((await incomplete.InitializeAsync()).IsReady);
        await using (SqliteConnection connection = await OpenRawAsync(incomplete.DatabasePath))
        {
            await ExecuteAsync(connection, "DROP INDEX ix_domain_events_aggregate;");
        }

        SqliteDatabaseInitialization missing = await incomplete.InitializeAsync();
        Assert.Equal("sqlite.schema_incomplete", missing.ReasonCode);
    }

    [Fact]
    public async Task SqlConstraintsEnforceForeignKeysJsonAndAppendOnlyHistory()
    {
        using TemporaryDirectory temporary = new();
        TooltailSqliteDatabase database = CreateDatabase(temporary);
        Assert.True((await database.InitializeAsync()).IsReady);
        await using SqliteConnection connection = await OpenRawAsync(database.DatabasePath);
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;");

        await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(
                connection,
                "INSERT INTO companions " +
                "(companion_id, display_name, created_utc, identity_schema_version, presentation_json) " +
                "VALUES ('11111111-1111-4111-8111-111111111111', 'Companion', " +
                "'2026-07-16T08:00:00.0000000Z', 1, 'not-json');"));
        await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(
                connection,
                "INSERT INTO resource_grants " +
                "(grant_id, companion_id, resource_type, root_identity, capabilities_json, " +
                "issued_utc, grant_fingerprint) VALUES " +
                "('22222222-2222-4222-8222-222222222222', " +
                "'33333333-3333-4333-8333-333333333333', 'local_folder', 'root', '[]', " +
                "'2026-07-16T08:00:00.0000000Z', " +
                "'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa');"));
        await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(
                connection,
                "UPDATE schema_migrations SET application_version = 'changed' WHERE version = 1;"));
        Assert.Equal(
            "0.1.0-test",
            await ScalarStringAsync(
                connection,
                "SELECT application_version FROM schema_migrations WHERE version = 1;"));
    }

    [Fact]
    public async Task CorruptDatabaseBytesArePreservedAndCancellationPropagates()
    {
        using TemporaryDirectory corruptTemporary = new();
        string corruptPath = corruptTemporary.CreateTextFile(
            "tooltail.db",
            "not a sqlite database; preserve these bytes");
        byte[] original = await File.ReadAllBytesAsync(corruptPath);
        TooltailSqliteDatabase corrupt = CreateDatabase(corruptPath);

        SqliteDatabaseInitialization failed = await corrupt.InitializeAsync();

        Assert.Equal(SqliteDatabaseStatus.ReadOnlyRecoveryRequired, failed.Status);
        Assert.Equal("sqlite.open_failed", failed.ReasonCode);
        Assert.Equal(original, await File.ReadAllBytesAsync(corruptPath));

        using TemporaryDirectory cancelledTemporary = new();
        TooltailSqliteDatabase cancelled = CreateDatabase(cancelledTemporary);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancelled.InitializeAsync(cancellation.Token));
        Assert.False(File.Exists(cancelled.DatabasePath));
    }

    [Fact]
    public void OptionsRejectInMemoryAndUnboundedAuthorityValues()
    {
        Assert.Throws<ArgumentException>(
            () => new SqliteDatabaseOptions(":memory:", "0.1.0"));
        Assert.Throws<ArgumentException>(
            () => new SqliteDatabaseOptions("tooltail.db", new string('v', 65)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SqliteDatabaseOptions(
                "tooltail.db",
                "0.1.0",
                busyTimeoutMilliseconds: 60_001));
    }

    private static TooltailSqliteDatabase CreateDatabase(TemporaryDirectory temporary) =>
        CreateDatabase(Path.Combine(temporary.Path, "state", "tooltail.db"));

    private static TooltailSqliteDatabase CreateDatabase(string path) =>
        new(
            new SqliteDatabaseOptions(path, "0.1.0-test"),
            new FixedClock(Now),
            new FixedIdGenerator());

    private static async Task<SqliteConnection> OpenRawAsync(string path)
    {
        SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarInt64Async(
        SqliteConnection connection,
        string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(
        SqliteConnection connection,
        string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = await command.ExecuteScalarAsync();
        return Convert.ToString(
            value,
            System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FixedIdGenerator : IIdGenerator
    {
        public Guid NewId() =>
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    }

    private static readonly HashSet<string> ExpectedTables =
        new(
            [
                "schema_migrations",
                "companions",
                "window_leases",
                "resource_grants",
                "folder_snapshots",
                "teaching_episodes",
                "demonstration_examples",
                "skills",
                "skill_versions",
                "skill_evidence",
                "execution_plans",
                "approvals",
                "executions",
                "execution_journal_events",
                "receipts",
                "agent_runs",
                "domain_events",
            ],
            StringComparer.Ordinal);
}
