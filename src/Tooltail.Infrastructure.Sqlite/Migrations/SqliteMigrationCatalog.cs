using System.Collections.ObjectModel;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Tooltail.Infrastructure.Sqlite.Migrations;

internal static class SqliteMigrationCatalog
{
    private const string InitialResource =
        "Tooltail.Infrastructure.Sqlite.Migrations.0001_initial.sql";

    private static readonly IReadOnlyList<SqliteMigration> Catalog =
        new ReadOnlyCollection<SqliteMigration>(
            [Load(version: 1, InitialResource, requiresBackup: false)]);

    public static IReadOnlyList<SqliteMigration> All => Catalog;

    public static int CurrentVersion => Catalog[^1].Version;

    public static IReadOnlySet<string> RequiredSchemaObjects { get; } =
        new HashSet<string>(
            [
                "table:schema_migrations",
                "table:companions",
                "table:window_leases",
                "table:resource_grants",
                "table:folder_snapshots",
                "table:teaching_episodes",
                "table:demonstration_examples",
                "table:skills",
                "table:skill_versions",
                "table:skill_evidence",
                "table:execution_plans",
                "table:approvals",
                "table:executions",
                "table:execution_journal_events",
                "table:receipts",
                "table:agent_runs",
                "table:domain_events",
                "index:ux_skill_versions_skill_hash",
                "index:ux_execution_plans_fingerprint",
                "index:ix_execution_journal_events_recovery",
                "index:ix_domain_events_aggregate",
                "trigger:schema_migrations_no_update",
                "trigger:schema_migrations_no_delete",
                "trigger:execution_journal_events_no_update",
                "trigger:execution_journal_events_no_delete",
                "trigger:receipts_no_update",
                "trigger:receipts_no_delete",
                "trigger:domain_events_no_update",
                "trigger:domain_events_no_delete",
                "trigger:skill_versions_immutable_core",
            ],
            StringComparer.Ordinal);

    private static SqliteMigration Load(
        int version,
        string resourceName,
        bool requiresBackup)
    {
        Assembly assembly = typeof(SqliteMigrationCatalog).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(resourceName) ??
            throw new InvalidOperationException(
                $"Embedded migration resource '{resourceName}' is missing.");
        using StreamReader reader = new(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true);
        string normalized = Normalize(reader.ReadToEnd());
        string checksum = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return new SqliteMigration(
            version,
            resourceName,
            normalized,
            checksum,
            requiresBackup);
    }

    private static string Normalize(string sql) =>
        sql.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd() + "\n";
}
