using System.Collections.ObjectModel;

namespace Tooltail.Infrastructure.Sqlite;

public enum SqliteDatabaseStatus
{
    Ready,
    ReadOnlyRecoveryRequired,
}

public sealed record SqliteDatabaseInitialization
{
    private SqliteDatabaseInitialization(
        SqliteDatabaseStatus status,
        string reasonCode,
        int schemaVersion,
        string? journalMode,
        bool backupCreated,
        IEnumerable<int> appliedVersions)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentOutOfRangeException.ThrowIfNegative(schemaVersion);
        Status = status;
        ReasonCode = reasonCode;
        SchemaVersion = schemaVersion;
        JournalMode = journalMode;
        BackupCreated = backupCreated;
        AppliedVersions = new ReadOnlyCollection<int>(appliedVersions.ToArray());
    }

    public SqliteDatabaseStatus Status { get; }

    public string ReasonCode { get; }

    public int SchemaVersion { get; }

    public string? JournalMode { get; }

    public bool BackupCreated { get; }

    public IReadOnlyList<int> AppliedVersions { get; }

    public bool IsReady => Status == SqliteDatabaseStatus.Ready;

    internal static SqliteDatabaseInitialization Ready(
        int schemaVersion,
        string journalMode,
        bool backupCreated,
        IEnumerable<int> appliedVersions) =>
        new(
            SqliteDatabaseStatus.Ready,
            "sqlite.ready",
            schemaVersion,
            journalMode,
            backupCreated,
            appliedVersions);

    internal static SqliteDatabaseInitialization RecoveryRequired(
        string reasonCode,
        int schemaVersion = 0,
        string? journalMode = null,
        bool backupCreated = false) =>
        new(
            SqliteDatabaseStatus.ReadOnlyRecoveryRequired,
            reasonCode,
            schemaVersion,
            journalMode,
            backupCreated,
            []);
}
