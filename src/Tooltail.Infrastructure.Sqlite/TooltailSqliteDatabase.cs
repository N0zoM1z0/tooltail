using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Tooltail.Application.Abstractions;
using Tooltail.Infrastructure.Sqlite.Migrations;

namespace Tooltail.Infrastructure.Sqlite;

public sealed class TooltailSqliteDatabase
{
    private const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InitializationLocks =
        new(PathComparer);

    private readonly SqliteDatabaseOptions options;
    private readonly IClock clock;
    private readonly IIdGenerator idGenerator;
    private bool writeReady;

    public TooltailSqliteDatabase(
        SqliteDatabaseOptions options,
        IClock clock,
        IIdGenerator idGenerator)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(idGenerator);
        this.options = options;
        this.clock = clock;
        this.idGenerator = idGenerator;
    }

    public string DatabasePath => options.DatabasePath;

    public async Task<SqliteDatabaseInitialization> InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref writeReady, false);
        SemaphoreSlim initializationLock = InitializationLocks.GetOrAdd(
            options.DatabasePath,
            static _ => new SemaphoreSlim(1, 1));
        await initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SqliteDatabaseInitialization initialized = await InitializeCoreAsync(
                cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref writeReady, initialized.IsReady);
            return initialized;
        }
        catch (Exception exception) when (IsExpectedInitializationFailure(exception))
        {
            return SqliteDatabaseInitialization.RecoveryRequired("sqlite.open_failed");
        }
        finally
        {
            initializationLock.Release();
        }
    }

    public async ValueTask<SqliteConnection> OpenReadOnlyAsync(
        CancellationToken cancellationToken = default)
    {
        SqliteConnection connection = new(CreateConnectionString(SqliteOpenMode.ReadOnly));
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(
                connection,
                $"PRAGMA busy_timeout={options.BusyTimeoutMilliseconds};" +
                "PRAGMA foreign_keys=ON;" +
                "PRAGMA query_only=ON;" +
                "PRAGMA trusted_schema=OFF;",
                cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask<SqliteConnection> OpenReadWriteAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Volatile.Read(ref writeReady))
        {
            throw new InvalidOperationException(
                "SQLite write access requires a successful initialization check.");
        }

        SqliteConnection connection = new(CreateConnectionString(SqliteOpenMode.ReadWrite));
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(
                connection,
                setJournalMode: false,
                cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<SqliteDatabaseInitialization> InitializeCoreAsync(
        CancellationToken cancellationToken)
    {
        DateTimeOffset nowUtc = clock.UtcNow;
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            return SqliteDatabaseInitialization.RecoveryRequired("sqlite.non_utc_time");
        }

        string? directory = Path.GetDirectoryName(options.DatabasePath);
        if (string.IsNullOrEmpty(directory))
        {
            return SqliteDatabaseInitialization.RecoveryRequired("sqlite.path_invalid");
        }

        bool databaseExisted = File.Exists(options.DatabasePath);
        Directory.CreateDirectory(directory);
        await using SqliteConnection connection = new(
            CreateConnectionString(SqliteOpenMode.ReadWriteCreate));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        string journalMode = await ConfigureConnectionAsync(
            connection,
            setJournalMode: true,
            cancellationToken).ConfigureAwait(false);
        ApplyPrivateFileMode();

        if (!await IsIntegrityValidAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return SqliteDatabaseInitialization.RecoveryRequired(
                "sqlite.integrity_failed",
                journalMode: journalMode);
        }

        SchemaInspection inspection = await InspectSchemaAsync(
            connection,
            cancellationToken).ConfigureAwait(false);
        SchemaValidation validation = ValidateMigrationHistory(inspection);
        if (!validation.IsValid)
        {
            return SqliteDatabaseInitialization.RecoveryRequired(
                validation.ReasonCode,
                validation.SchemaVersion,
                journalMode);
        }

        IReadOnlyList<SqliteMigration> pending =
            SqliteMigrationCatalog.All.Skip(validation.SchemaVersion).ToArray();
        bool backupCreated = false;
        if (databaseExisted && pending.Any(static migration => migration.RequiresBackup))
        {
            backupCreated = await CreateBackupAsync(
                connection,
                validation.SchemaVersion,
                nowUtc,
                cancellationToken).ConfigureAwait(false);
            if (!backupCreated)
            {
                return SqliteDatabaseInitialization.RecoveryRequired(
                    "sqlite.backup_failed",
                    validation.SchemaVersion,
                    journalMode);
            }
        }

        List<int> appliedVersions = [];
        bool transactionActive = false;
        try
        {
            await ExecuteNonQueryAsync(
                connection,
                "BEGIN IMMEDIATE;",
                cancellationToken).ConfigureAwait(false);
            transactionActive = true;

            inspection = await InspectSchemaAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            validation = ValidateMigrationHistory(inspection);
            if (!validation.IsValid)
            {
                await RollbackAsync(connection).ConfigureAwait(false);
                transactionActive = false;
                return SqliteDatabaseInitialization.RecoveryRequired(
                    validation.ReasonCode,
                    validation.SchemaVersion,
                    journalMode,
                    backupCreated);
            }

            pending = SqliteMigrationCatalog.All
                .Skip(validation.SchemaVersion)
                .ToArray();
            if (databaseExisted &&
                !backupCreated &&
                pending.Any(static migration => migration.RequiresBackup))
            {
                await RollbackAsync(connection).ConfigureAwait(false);
                transactionActive = false;
                return SqliteDatabaseInitialization.RecoveryRequired(
                    "sqlite.backup_required",
                    validation.SchemaVersion,
                    journalMode);
            }

            foreach (SqliteMigration migration in pending)
            {
                await ApplyMigrationAsync(
                    connection,
                    migration,
                    nowUtc,
                    cancellationToken).ConfigureAwait(false);
                appliedVersions.Add(migration.Version);
            }

            if (!await HasRequiredSchemaAsync(connection, cancellationToken)
                    .ConfigureAwait(false))
            {
                await RollbackAsync(connection).ConfigureAwait(false);
                transactionActive = false;
                return SqliteDatabaseInitialization.RecoveryRequired(
                    "sqlite.schema_incomplete",
                    validation.SchemaVersion,
                    journalMode,
                    backupCreated);
            }

            if (!await HasValidForeignKeysAsync(connection, cancellationToken)
                    .ConfigureAwait(false))
            {
                await RollbackAsync(connection).ConfigureAwait(false);
                transactionActive = false;
                return SqliteDatabaseInitialization.RecoveryRequired(
                    "sqlite.foreign_key_violation",
                    validation.SchemaVersion,
                    journalMode,
                    backupCreated);
            }

            await ExecuteNonQueryAsync(connection, "COMMIT;", cancellationToken)
                .ConfigureAwait(false);
            transactionActive = false;
        }
        catch (OperationCanceledException)
        {
            if (transactionActive)
            {
                await RollbackAsync(connection).ConfigureAwait(false);
            }

            throw;
        }
        catch (SqliteException)
        {
            if (transactionActive)
            {
                await RollbackAsync(connection).ConfigureAwait(false);
            }

            return SqliteDatabaseInitialization.RecoveryRequired(
                "sqlite.migration_failed",
                validation.SchemaVersion,
                journalMode,
                backupCreated);
        }

        return SqliteDatabaseInitialization.Ready(
            SqliteMigrationCatalog.CurrentVersion,
            journalMode,
            backupCreated,
            appliedVersions);
    }

    private async Task<string> ConfigureConnectionAsync(
        SqliteConnection connection,
        bool setJournalMode,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            connection,
            $"PRAGMA busy_timeout={options.BusyTimeoutMilliseconds};" +
            "PRAGMA foreign_keys=ON;" +
            "PRAGMA synchronous=FULL;" +
            "PRAGMA trusted_schema=OFF;" +
            "PRAGMA recursive_triggers=ON;" +
            "PRAGMA secure_delete=ON;" +
            "PRAGMA temp_store=MEMORY;",
            cancellationToken).ConfigureAwait(false);
        if (!setJournalMode)
        {
            return await ReadJournalModeAsync(connection, cancellationToken)
                .ConfigureAwait(false);
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        object? result = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToString(result, CultureInfo.InvariantCulture)?.ToLowerInvariant() ??
            "unknown";
    }

    private static async Task<string> ReadJournalModeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        object? result = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToString(result, CultureInfo.InvariantCulture)?.ToLowerInvariant() ??
            "unknown";
    }

    private static async Task<bool> IsIntegrityValidAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check(1);";
        object? result = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return string.Equals(
            Convert.ToString(result, CultureInfo.InvariantCulture),
            "ok",
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<SchemaInspection> InspectSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        HashSet<string> objects = new(StringComparer.Ordinal);
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT type, name FROM sqlite_schema " +
                "WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name;";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(
                cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                objects.Add($"{reader.GetString(0)}:{reader.GetString(1)}");
            }
        }

        if (!objects.Contains("table:schema_migrations"))
        {
            return new SchemaInspection(objects, []);
        }

        List<AppliedMigration> migrations = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT version, applied_utc, application_version, checksum " +
                "FROM schema_migrations ORDER BY version;";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(
                cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                migrations.Add(
                    new AppliedMigration(
                        reader.GetInt32(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3)));
            }
        }

        return new SchemaInspection(objects, migrations);
    }

    private static SchemaValidation ValidateMigrationHistory(
        SchemaInspection inspection)
    {
        bool hasMigrationTable =
            inspection.SchemaObjects.Contains("table:schema_migrations");
        if (!hasMigrationTable)
        {
            return inspection.SchemaObjects.Count == 0
                ? SchemaValidation.Valid(schemaVersion: 0)
                : SchemaValidation.Invalid("sqlite.schema_unmanaged");
        }

        if (inspection.Migrations.Count == 0)
        {
            return SchemaValidation.Invalid("sqlite.migration_history_invalid");
        }

        for (int index = 0; index < inspection.Migrations.Count; index++)
        {
            AppliedMigration applied = inspection.Migrations[index];
            int expectedVersion = index + 1;
            if (applied.Version != expectedVersion)
            {
                return SchemaValidation.Invalid(
                    "sqlite.migration_gap",
                    index);
            }

            if (applied.Version > SqliteMigrationCatalog.CurrentVersion)
            {
                return SchemaValidation.Invalid(
                    "sqlite.migration_unknown",
                    applied.Version);
            }

            SqliteMigration known = SqliteMigrationCatalog.All[index];
            if (!string.Equals(
                    applied.Checksum,
                    known.Checksum,
                    StringComparison.Ordinal))
            {
                return SchemaValidation.Invalid(
                    "sqlite.migration_checksum_mismatch",
                    index);
            }

            if (!DateTimeOffset.TryParseExact(
                    applied.AppliedUtc,
                    UtcFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset appliedUtc) ||
                appliedUtc.Offset != TimeSpan.Zero ||
                string.IsNullOrWhiteSpace(applied.ApplicationVersion) ||
                applied.ApplicationVersion.Length > 64)
            {
                return SchemaValidation.Invalid(
                    "sqlite.migration_history_invalid",
                    index);
            }
        }

        return SchemaValidation.Valid(inspection.Migrations.Count);
    }

    private async Task ApplyMigrationAsync(
        SqliteConnection connection,
        SqliteMigration migration,
        DateTimeOffset appliedUtc,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, migration.Sql, cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO schema_migrations " +
            "(version, applied_utc, application_version, checksum) " +
            "VALUES ($version, $applied_utc, $application_version, $checksum);";
        command.Parameters.AddWithValue("$version", migration.Version);
        command.Parameters.AddWithValue("$applied_utc", FormatUtc(appliedUtc));
        command.Parameters.AddWithValue("$application_version", options.ApplicationVersion);
        command.Parameters.AddWithValue("$checksum", migration.Checksum);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> HasRequiredSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        SchemaInspection inspection = await InspectSchemaAsync(
            connection,
            cancellationToken).ConfigureAwait(false);
        return SqliteMigrationCatalog.RequiredSchemaObjects.IsSubsetOf(
            inspection.SchemaObjects);
    }

    private static async Task<bool> HasValidForeignKeysAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        return !await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> CreateBackupAsync(
        SqliteConnection source,
        int sourceVersion,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        string backupPath =
            $"{options.DatabasePath}.backup-v{sourceVersion}-" +
            $"{nowUtc:yyyyMMddTHHmmssfffffffZ}-{idGenerator.NewId():N}";
        try
        {
            if (File.Exists(backupPath))
            {
                return false;
            }

            await using SqliteConnection destination = new(
                CreateConnectionString(SqliteOpenMode.ReadWriteCreate, backupPath));
            await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
            source.BackupDatabase(destination);
            ApplyPrivateFileMode(backupPath);
            return true;
        }
        catch (Exception exception) when (IsExpectedInitializationFailure(exception))
        {
            return false;
        }
    }

    private string CreateConnectionString(
        SqliteOpenMode mode,
        string? databasePath = null) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath ?? options.DatabasePath,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = checked((int)Math.Ceiling(
                options.BusyTimeoutMilliseconds / 1000d)),
        }.ToString();

    private void ApplyPrivateFileMode() => ApplyPrivateFileMode(options.DatabasePath);

    private static void ApplyPrivateFileMode(string path)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(path))
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RollbackAsync(SqliteConnection connection)
    {
        try
        {
            await ExecuteNonQueryAsync(connection, "ROLLBACK;", CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // The original migration failure remains authoritative.
        }
    }

    private static bool IsExpectedInitializationFailure(Exception exception) =>
        exception is SqliteException or IOException or UnauthorizedAccessException or
            System.Security.SecurityException or NotSupportedException or ArgumentException;

    private static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString(UtcFormat, CultureInfo.InvariantCulture);

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed record AppliedMigration(
        int Version,
        string AppliedUtc,
        string ApplicationVersion,
        string Checksum);

    private sealed record SchemaInspection(
        IReadOnlySet<string> SchemaObjects,
        IReadOnlyList<AppliedMigration> Migrations);

    private sealed record SchemaValidation(
        bool IsValid,
        string ReasonCode,
        int SchemaVersion)
    {
        public static SchemaValidation Valid(int schemaVersion) =>
            new(true, "sqlite.schema_valid", schemaVersion);

        public static SchemaValidation Invalid(
            string reasonCode,
            int schemaVersion = 0) =>
            new(false, reasonCode, schemaVersion);
    }
}
