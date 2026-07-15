namespace Tooltail.Infrastructure.Sqlite;

public sealed record SqliteDatabaseOptions
{
    public SqliteDatabaseOptions(
        string databasePath,
        string applicationVersion,
        int busyTimeoutMilliseconds = 5_000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationVersion);
        if (string.Equals(databasePath, ":memory:", StringComparison.OrdinalIgnoreCase) ||
            databasePath.Contains('\0', StringComparison.Ordinal) ||
            databasePath.Length > 32_768)
        {
            throw new ArgumentException(
                "The SQLite database path is invalid or unbounded.",
                nameof(databasePath));
        }

        if (applicationVersion.Length > 64 ||
            applicationVersion.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The application version is invalid or unbounded.",
                nameof(applicationVersion));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(busyTimeoutMilliseconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(busyTimeoutMilliseconds, 60_000);
        string fullPath = Path.GetFullPath(databasePath);
        if (string.IsNullOrEmpty(Path.GetFileName(fullPath)))
        {
            throw new ArgumentException(
                "Tooltail state requires a named on-disk SQLite database.",
                nameof(databasePath));
        }

        DatabasePath = fullPath;
        ApplicationVersion = applicationVersion;
        BusyTimeoutMilliseconds = busyTimeoutMilliseconds;
    }

    public string DatabasePath { get; }

    public string ApplicationVersion { get; }

    public int BusyTimeoutMilliseconds { get; }
}
