using Microsoft.Data.Sqlite;
using Tooltail.Application.Abstractions;
using Tooltail.Domain.Skills;

namespace Tooltail.Infrastructure.Sqlite;

public sealed partial class SqliteFileSkillStateStore
{
    private const int MaximumCapsuleSkillVersions = 500;

    private ValueTask<StateWriteResult> ImportCapsuleCoreAsync(
        CapsuleImportStateRecord import,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(import);
        if (!IsValidCapsuleImport(import))
        {
            return ValueTask.FromResult(
                StateWriteResult.Failure("persistence.capsule_import_invalid"));
        }

        return WriteAsync(
            async (connection, token) =>
            {
                await RequirePristineCompanionAsync(
                    connection,
                    import,
                    token).ConfigureAwait(false);
                await ReplacePristineCompanionAsync(
                    connection,
                    import,
                    token).ConfigureAwait(false);

                foreach (SkillVersionStateRecord version in import.SkillVersions
                             .OrderBy(static record => record.Version.SkillId.Value)
                             .ThenBy(static record => record.Version.Number.Value))
                {
                    await UpsertSkillAsync(connection, version, token)
                        .ConfigureAwait(false);
                    string versionId = SqliteStorePrimitives.SkillVersionKey(
                        version.Version.SkillId,
                        version.Version.Number);
                    await InsertSkillVersionAsync(connection, version, versionId, token)
                        .ConfigureAwait(false);
                    if (version.MakeCurrent)
                    {
                        await AdvanceCurrentSkillVersionAsync(
                            connection,
                            version.Version.SkillId,
                            version.Version.Number,
                            versionId,
                            token).ConfigureAwait(false);
                    }
                }
            },
            cancellationToken);
    }

    private static bool IsValidCapsuleImport(CapsuleImportStateRecord import)
    {
        CompanionStateRecord companion = import.ImportedCompanion;
        if (import.ExpectedEmptyCompanionId.Value == Guid.Empty ||
            companion.Id.Value == Guid.Empty ||
            !IsBoundedText(companion.DisplayName, 200) ||
            companion.CreatedUtc.Offset != TimeSpan.Zero ||
            companion.IdentitySchemaVersion != 1 ||
            !SqliteStorePrimitives.IsValidJson(companion.PresentationJson) ||
            import.SkillVersions is null ||
            import.SkillVersions.Count > MaximumCapsuleSkillVersions)
        {
            return false;
        }

        HashSet<(Guid SkillId, int Version)> keys = [];
        foreach (SkillVersionStateRecord record in import.SkillVersions)
        {
            SkillVersion version = record.Version;
            if (record.CompanionId != companion.Id ||
                version.SkillId.Value == Guid.Empty ||
                !keys.Add((version.SkillId.Value, version.Number.Value)) ||
                !IsBoundedText(record.DisplayName, 200) ||
                record.SkillCreatedUtc.Offset != TimeSpan.Zero ||
                version.CreatedAt.Offset != TimeSpan.Zero ||
                version.CreatedAt < record.SkillCreatedUtc ||
                version.Lifecycle != SkillLifecycleState.Stale ||
                !IsBoundedText(record.SchemaVersion, 64) ||
                !SqliteStorePrimitives.IsValidJson(record.SkillSpecJson) ||
                !SqliteStorePrimitives.HashMatches(
                    record.SkillSpecJson,
                    version.SpecificationHash) ||
                !IsBoundedText(record.CompilerId, 128) ||
                !IsBoundedText(version.CompilerVersion, 64) ||
                !IsBoundedText(version.MinimumExecutorVersion, 64) ||
                record.ApprovedUtc is not null ||
                !SqliteStorePrimitives.IsValidJson(
                    record.SemanticDiffJson,
                    nullable: true))
            {
                return false;
            }
        }

        foreach (IGrouping<Guid, SkillVersionStateRecord> skill in
                 import.SkillVersions.GroupBy(static record => record.Version.SkillId.Value))
        {
            SkillVersionStateRecord[] ordered = skill
                .OrderBy(static record => record.Version.Number.Value)
                .ToArray();
            if (ordered.Count(static record => record.MakeCurrent) != 1 ||
                !ordered[^1].MakeCurrent ||
                ordered.Any(record => record.SkillCreatedUtc != ordered[0].SkillCreatedUtc))
            {
                return false;
            }

            for (int index = 0; index < ordered.Length; index++)
            {
                int expectedVersion = index + 1;
                SkillVersionNumber? expectedParent = index == 0
                    ? null
                    : new SkillVersionNumber(index);
                if (ordered[index].Version.Number.Value != expectedVersion ||
                    ordered[index].Version.Parent != expectedParent ||
                    (index > 0 && ordered[index].Version.CreatedAt <
                        ordered[index - 1].Version.CreatedAt))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static async Task RequirePristineCompanionAsync(
        SqliteConnection connection,
        CapsuleImportStateRecord import,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT " +
            "(SELECT COUNT(*) FROM companions), " +
            "(SELECT COUNT(*) FROM companions WHERE companion_id = $expected), " +
            "(SELECT COUNT(*) FROM window_leases) + " +
            "(SELECT COUNT(*) FROM resource_grants) + " +
            "(SELECT COUNT(*) FROM folder_snapshots) + " +
            "(SELECT COUNT(*) FROM teaching_episodes) + " +
            "(SELECT COUNT(*) FROM demonstration_examples) + " +
            "(SELECT COUNT(*) FROM skills) + " +
            "(SELECT COUNT(*) FROM skill_versions) + " +
            "(SELECT COUNT(*) FROM skill_evidence) + " +
            "(SELECT COUNT(*) FROM execution_plans) + " +
            "(SELECT COUNT(*) FROM approvals) + " +
            "(SELECT COUNT(*) FROM executions) + " +
            "(SELECT COUNT(*) FROM execution_journal_events) + " +
            "(SELECT COUNT(*) FROM receipts) + " +
            "(SELECT COUNT(*) FROM agent_runs) + " +
            "(SELECT COUNT(*) FROM domain_events);";
        command.Parameters.AddWithValue(
            "$expected",
            SqliteStorePrimitives.FormatGuid(import.ExpectedEmptyCompanionId.Value));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetInt64(0) != 1 ||
            reader.GetInt64(1) != 1 ||
            reader.GetInt64(2) != 0)
        {
            throw new PersistenceConflictException(
                "persistence.capsule_import_state_not_pristine");
        }
    }

    private static async Task ReplacePristineCompanionAsync(
        SqliteConnection connection,
        CapsuleImportStateRecord import,
        CancellationToken cancellationToken)
    {
        await using (SqliteCommand delete = connection.CreateCommand())
        {
            delete.CommandText =
                "DELETE FROM companions WHERE companion_id = $expected;";
            delete.Parameters.AddWithValue(
                "$expected",
                SqliteStorePrimitives.FormatGuid(import.ExpectedEmptyCompanionId.Value));
            if (await delete.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false) != 1)
            {
                throw new PersistenceConflictException(
                    "persistence.capsule_import_companion_changed");
            }
        }

        CompanionStateRecord companion = import.ImportedCompanion;
        await using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO companions " +
            "(companion_id, display_name, created_utc, identity_schema_version, presentation_json) " +
            "VALUES ($id, $name, $created, $schema, $presentation);";
        insert.Parameters.AddWithValue(
            "$id",
            SqliteStorePrimitives.FormatGuid(companion.Id.Value));
        insert.Parameters.AddWithValue("$name", companion.DisplayName);
        insert.Parameters.AddWithValue(
            "$created",
            SqliteStorePrimitives.FormatUtc(companion.CreatedUtc));
        insert.Parameters.AddWithValue("$schema", companion.IdentitySchemaVersion);
        insert.Parameters.AddWithValue("$presentation", companion.PresentationJson);
        if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new PersistenceConflictException(
                "persistence.capsule_import_companion_write_failed");
        }
    }
}
