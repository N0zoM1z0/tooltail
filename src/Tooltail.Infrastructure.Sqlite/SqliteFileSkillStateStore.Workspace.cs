using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tooltail.Application.Abstractions;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;

namespace Tooltail.Infrastructure.Sqlite;

public sealed partial class SqliteFileSkillStateStore
{
    private const int MaximumWorkspaceCompanions = 100;
    private const int MaximumWorkspaceGrants = 100;
    private const int MaximumWorkspaceSkills = 500;
    private const int MaximumWorkspaceEpisodes = 100;
    private const int MaximumWorkspaceExecutions = 100;

    public async ValueTask<StateReadResult<IReadOnlyList<CompanionStateRecord>>>
        ListCompanionsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = await database.OpenReadOnlyAsync(
                cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT companion_id FROM companions ORDER BY created_utc, companion_id " +
                "LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", MaximumWorkspaceCompanions + 1);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(
                cancellationToken).ConfigureAwait(false);
            List<CompanionId> ids = [];
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (ids.Count == MaximumWorkspaceCompanions)
                {
                    throw new PersistenceConflictException(
                        "persistence.workspace_companion_bound_exceeded");
                }

                ids.Add(new CompanionId(Guid.Parse(reader.GetString(0))));
            }

            await reader.DisposeAsync().ConfigureAwait(false);
            List<CompanionStateRecord> companions = new(ids.Count);
            foreach (CompanionId id in ids)
            {
                companions.Add(
                    await ReadCompanionAsync(connection, id, cancellationToken)
                        .ConfigureAwait(false));
            }

            return StateReadResult.Success<IReadOnlyList<CompanionStateRecord>>(
                companions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedStoreFailure(exception))
        {
            return StateReadResult.Failure<IReadOnlyList<CompanionStateRecord>>(
                SqliteStorePrimitives.MapFailure(exception));
        }
    }

    public async ValueTask<StateReadResult<IReadOnlyList<SkillVersionStateRecord>>>
        LoadSkillVersionsAsync(
            SkillId skillId,
            CancellationToken cancellationToken = default)
    {
        if (skillId.Value == Guid.Empty)
        {
            return StateReadResult.Failure<IReadOnlyList<SkillVersionStateRecord>>(
                "persistence.skill_id_invalid");
        }

        try
        {
            SkillVersionNumber[] versions;
            await using (SqliteConnection connection = await database.OpenReadOnlyAsync(
                             cancellationToken).ConfigureAwait(false))
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    "SELECT version_number FROM skill_versions WHERE skill_id = $skill " +
                    "ORDER BY version_number LIMIT $limit;";
                command.Parameters.AddWithValue(
                    "$skill",
                    SqliteStorePrimitives.FormatGuid(skillId.Value));
                command.Parameters.AddWithValue("$limit", MaximumWorkspaceSkills + 1);
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(
                    cancellationToken).ConfigureAwait(false);
                List<SkillVersionNumber> materialized = [];
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (materialized.Count == MaximumWorkspaceSkills)
                    {
                        throw new PersistenceConflictException(
                            "persistence.workspace_skill_version_bound_exceeded");
                    }

                    materialized.Add(new SkillVersionNumber(reader.GetInt32(0)));
                }

                versions = materialized.ToArray();
            }

            List<SkillVersionStateRecord> records = new(versions.Length);
            foreach (SkillVersionNumber version in versions)
            {
                StateReadResult<SkillVersionStateRecord> loaded =
                    await LoadSkillVersionCoreAsync(
                        skillId,
                        version,
                        cancellationToken).ConfigureAwait(false);
                if (!loaded.IsSuccess)
                {
                    return StateReadResult.Failure<IReadOnlyList<SkillVersionStateRecord>>(
                        loaded.ReasonCode);
                }

                records.Add(loaded.Value!);
            }

            if (records.Count(static record => record.MakeCurrent) > 1)
            {
                return StateReadResult.Failure<IReadOnlyList<SkillVersionStateRecord>>(
                    "persistence.skill_current_version_corrupt");
            }

            return StateReadResult.Success<IReadOnlyList<SkillVersionStateRecord>>(
                records);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedStoreFailure(exception))
        {
            return StateReadResult.Failure<IReadOnlyList<SkillVersionStateRecord>>(
                SqliteStorePrimitives.MapFailure(exception));
        }
    }

    public async ValueTask<StateReadResult<FileSkillWorkspaceStateRecord>>
        LoadWorkspaceStateAsync(
            CompanionId companionId,
            CancellationToken cancellationToken = default)
    {
        if (companionId.Value == Guid.Empty)
        {
            return StateReadResult.Failure<FileSkillWorkspaceStateRecord>(
                "persistence.companion_id_invalid");
        }

        try
        {
            CompanionStateRecord companion;
            LocalFolderGrantStateRecord[] grants;
            (SkillId SkillId, SkillVersionNumber Version)[] skillKeys;
            TeachingEpisodeSummaryStateRecord[] episodes;
            ExecutionSummaryStateRecord[] executions;
            await using (SqliteConnection connection = await database.OpenReadOnlyAsync(
                             cancellationToken).ConfigureAwait(false))
            {
                companion = await ReadCompanionAsync(
                    connection,
                    companionId,
                    cancellationToken).ConfigureAwait(false);
                grants = await ReadGrantsAsync(
                    connection,
                    companionId,
                    cancellationToken).ConfigureAwait(false);
                skillKeys = await ReadCurrentSkillKeysAsync(
                    connection,
                    companionId,
                    cancellationToken).ConfigureAwait(false);
                episodes = await ReadTeachingEpisodesAsync(
                    connection,
                    companionId,
                    cancellationToken).ConfigureAwait(false);
                executions = await ReadExecutionsAsync(
                    connection,
                    companionId,
                    cancellationToken).ConfigureAwait(false);
            }

            List<SkillVersionStateRecord> skills = new(skillKeys.Length);
            foreach ((SkillId skillId, SkillVersionNumber version) in skillKeys)
            {
                StateReadResult<SkillVersionStateRecord> loaded =
                    await LoadSkillVersionCoreAsync(
                        skillId,
                        version,
                        cancellationToken).ConfigureAwait(false);
                if (!loaded.IsSuccess ||
                    loaded.Value!.CompanionId != companionId ||
                    !loaded.Value.MakeCurrent)
                {
                    return StateReadResult.Failure<FileSkillWorkspaceStateRecord>(
                        loaded.IsSuccess
                            ? "persistence.workspace_changed"
                            : loaded.ReasonCode);
                }

                skills.Add(loaded.Value);
            }

            foreach (ExecutionSummaryStateRecord execution in executions)
            {
                StateReadResult<StoredPlanDocument> plan =
                    await LoadPlanDocumentCoreAsync(
                        execution.PlanId,
                        cancellationToken).ConfigureAwait(false);
                if (!plan.IsSuccess ||
                    plan.Value!.SkillId != execution.SkillId ||
                    plan.Value.SkillVersion != execution.SkillVersion ||
                    plan.Value.GrantId != execution.GrantId)
                {
                    return StateReadResult.Failure<FileSkillWorkspaceStateRecord>(
                        "persistence.workspace_plan_invalid");
                }
            }

            return StateReadResult.Success(
                new FileSkillWorkspaceStateRecord(
                    companion,
                    grants,
                    skills,
                    episodes,
                    executions));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedStoreFailure(exception))
        {
            return StateReadResult.Failure<FileSkillWorkspaceStateRecord>(
                SqliteStorePrimitives.MapFailure(exception));
        }
    }

    private static async Task<CompanionStateRecord> ReadCompanionAsync(
        SqliteConnection connection,
        CompanionId companionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT display_name, created_utc, identity_schema_version, presentation_json " +
            "FROM companions WHERE companion_id = $companion;";
        command.Parameters.AddWithValue(
            "$companion",
            SqliteStorePrimitives.FormatGuid(companionId.Value));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new PersistenceConflictException("persistence.companion_not_found");
        }

        CompanionStateRecord companion = new(
            companionId,
            reader.GetString(0),
            SqliteStorePrimitives.ParseUtc(reader.GetString(1)),
            reader.GetInt32(2),
            reader.GetString(3));
        if (!IsBoundedText(companion.DisplayName, 200) ||
            companion.IdentitySchemaVersion < 1 ||
            !SqliteStorePrimitives.IsValidJson(companion.PresentationJson))
        {
            throw new PersistenceConflictException("persistence.companion_corrupt");
        }

        return companion;
    }

    private static async Task<LocalFolderGrantStateRecord[]> ReadGrantsAsync(
        SqliteConnection connection,
        CompanionId companionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT grant_id, root_identity, canonical_root_protected, capabilities_json, " +
            "issued_utc, expires_utc, revoked_utc, revocation_reason, grant_fingerprint " +
            "FROM resource_grants WHERE companion_id = $companion " +
            "AND resource_type = 'local_folder' ORDER BY issued_utc DESC, grant_id " +
            "LIMIT $limit;";
        command.Parameters.AddWithValue(
            "$companion",
            SqliteStorePrimitives.FormatGuid(companionId.Value));
        command.Parameters.AddWithValue("$limit", MaximumWorkspaceGrants + 1);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        List<LocalFolderGrantStateRecord> grants = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (grants.Count == MaximumWorkspaceGrants)
            {
                throw new PersistenceConflictException(
                    "persistence.workspace_grant_bound_exceeded");
            }

            LocalFolderGrant grant = LocalFolderGrant.Issue(
                new GrantId(Guid.Parse(reader.GetString(0))),
                companionId,
                new ResourceRootIdentity(reader.GetString(1)),
                ParseCapabilities(reader.GetString(3)),
                SqliteStorePrimitives.ParseUtc(reader.GetString(4)),
                reader.IsDBNull(5)
                    ? null
                    : SqliteStorePrimitives.ParseUtc(reader.GetString(5)));
            if (!reader.IsDBNull(6))
            {
                Tooltail.Domain.Common.DomainResult<LocalFolderGrant> revoked = grant.Revoke(
                    SqliteStorePrimitives.ParseUtc(reader.GetString(6)),
                    reader.GetString(7));
                if (!revoked.IsSuccess)
                {
                    throw new PersistenceConflictException("persistence.grant_corrupt");
                }

                grant = revoked.Value!;
            }
            else if (!reader.IsDBNull(7))
            {
                throw new PersistenceConflictException("persistence.grant_corrupt");
            }

            byte[]? protectedRoot = reader.IsDBNull(2)
                ? null
                : ((byte[])reader.GetValue(2)).ToArray();
            if ((protectedRoot?.Length ?? 0) > 64 * 1024 ||
                !string.Equals(
                    SqliteStorePrimitives.GrantFingerprint(grant),
                    reader.GetString(8),
                    StringComparison.Ordinal))
            {
                throw new PersistenceConflictException("persistence.grant_corrupt");
            }

            grants.Add(new LocalFolderGrantStateRecord(grant, protectedRoot));
        }

        return grants.ToArray();
    }

    private static async Task<(SkillId SkillId, SkillVersionNumber Version)[]>
        ReadCurrentSkillKeysAsync(
            SqliteConnection connection,
            CompanionId companionId,
            CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT s.skill_id, v.version_number FROM skills s " +
            "JOIN skill_versions v ON v.skill_version_id = s.current_version_id " +
            "WHERE s.companion_id = $companion AND s.disabled_utc IS NULL " +
            "ORDER BY s.skill_id LIMIT $limit;";
        command.Parameters.AddWithValue(
            "$companion",
            SqliteStorePrimitives.FormatGuid(companionId.Value));
        command.Parameters.AddWithValue("$limit", MaximumWorkspaceSkills + 1);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        List<(SkillId SkillId, SkillVersionNumber Version)> skills = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (skills.Count == MaximumWorkspaceSkills)
            {
                throw new PersistenceConflictException(
                    "persistence.workspace_skill_bound_exceeded");
            }

            skills.Add(
                (new SkillId(Guid.Parse(reader.GetString(0))),
                 new SkillVersionNumber(reader.GetInt32(1))));
        }

        return skills.ToArray();
    }

    private static async Task<TeachingEpisodeSummaryStateRecord[]>
        ReadTeachingEpisodesAsync(
            SqliteConnection connection,
            CompanionId companionId,
            CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT e.episode_id, e.grant_id, e.started_utc, e.stopped_utc, " +
            "e.status, e.evidence_status, e.baseline_snapshot_ref, e.final_snapshot_ref, " +
            "e.invalid_reason, COUNT(x.example_id) FROM teaching_episodes e " +
            "LEFT JOIN demonstration_examples x ON x.episode_id = e.episode_id " +
            "WHERE e.companion_id = $companion GROUP BY e.episode_id " +
            "ORDER BY e.started_utc DESC, e.episode_id LIMIT $limit;";
        command.Parameters.AddWithValue(
            "$companion",
            SqliteStorePrimitives.FormatGuid(companionId.Value));
        command.Parameters.AddWithValue("$limit", MaximumWorkspaceEpisodes + 1);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        List<TeachingEpisodeSummaryStateRecord> episodes = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (episodes.Count == MaximumWorkspaceEpisodes)
            {
                throw new PersistenceConflictException(
                    "persistence.workspace_episode_bound_exceeded");
            }

            DateTimeOffset started = SqliteStorePrimitives.ParseUtc(reader.GetString(2));
            DateTimeOffset? stopped = reader.IsDBNull(3)
                ? null
                : SqliteStorePrimitives.ParseUtc(reader.GetString(3));
            int exampleCount = reader.GetInt32(9);
            if ((stopped is not null && stopped < started) || exampleCount is < 0 or > 5)
            {
                throw new PersistenceConflictException("persistence.episode_corrupt");
            }

            episodes.Add(
                new TeachingEpisodeSummaryStateRecord(
                    new TeachingEpisodeId(Guid.Parse(reader.GetString(0))),
                    new GrantId(Guid.Parse(reader.GetString(1))),
                    started,
                    stopped,
                    ParseEpisodeStatus(reader.GetString(4)),
                    ParseEvidenceStatus(reader.GetString(5)),
                    reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
                    reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    exampleCount));
        }

        return episodes.ToArray();
    }

    private static async Task<ExecutionSummaryStateRecord[]> ReadExecutionsAsync(
        SqliteConnection connection,
        CompanionId companionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT e.execution_id, e.plan_id, e.approval_id, e.journal_kind, " +
            "e.status, e.started_utc, e.completed_utc, v.skill_id, v.version_number, " +
            "p.grant_id, CASE WHEN r.receipt_id IS NULL THEN 0 ELSE 1 END " +
            "FROM executions e JOIN execution_plans p ON p.plan_id = e.plan_id " +
            "JOIN skill_versions v ON v.skill_version_id = p.skill_version_id " +
            "JOIN skills s ON s.skill_id = v.skill_id " +
            "LEFT JOIN receipts r ON r.execution_id = e.execution_id " +
            "WHERE s.companion_id = $companion " +
            "ORDER BY e.started_utc DESC, e.execution_id LIMIT $limit;";
        command.Parameters.AddWithValue(
            "$companion",
            SqliteStorePrimitives.FormatGuid(companionId.Value));
        command.Parameters.AddWithValue("$limit", MaximumWorkspaceExecutions + 1);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        List<ExecutionSummaryStateRecord> executions = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (executions.Count == MaximumWorkspaceExecutions)
            {
                throw new PersistenceConflictException(
                    "persistence.workspace_execution_bound_exceeded");
            }

            PersistedExecutionStatus status = ParseExecutionStatus(reader.GetString(4));
            DateTimeOffset started = SqliteStorePrimitives.ParseUtc(reader.GetString(5));
            DateTimeOffset? completed = reader.IsDBNull(6)
                ? null
                : SqliteStorePrimitives.ParseUtc(reader.GetString(6));
            bool hasReceipt = reader.GetInt32(10) == 1;
            if ((completed is not null && completed < started) ||
                (status == PersistedExecutionStatus.Verified &&
                 (completed is null || !hasReceipt)) ||
                (status != PersistedExecutionStatus.Verified && hasReceipt))
            {
                throw new PersistenceConflictException("persistence.execution_corrupt");
            }

            executions.Add(
                new ExecutionSummaryStateRecord(
                    new ExecutionId(Guid.Parse(reader.GetString(0))),
                    new PlanId(Guid.Parse(reader.GetString(1))),
                    new ApprovalId(Guid.Parse(reader.GetString(2))),
                    SqliteStorePrimitives.ParseJournalKind(reader.GetString(3)),
                    status,
                    started,
                    completed,
                    new SkillId(Guid.Parse(reader.GetString(7))),
                    new SkillVersionNumber(reader.GetInt32(8)),
                    new GrantId(Guid.Parse(reader.GetString(9))),
                    hasReceipt));
        }

        return executions.ToArray();
    }

    private static GrantCapability[] ParseCapabilities(string json)
    {
        string[]? values = JsonSerializer.Deserialize<string[]>(json);
        if (values is null || values.Length is < 1 or > 7 ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            throw new FormatException("Persisted grant capabilities are invalid.");
        }

        return values.Select(static value => value switch
        {
            "enumerate" => GrantCapability.Enumerate,
            "read_metadata" => GrantCapability.ReadMetadata,
            "read_content_hash" => GrantCapability.ReadContentHash,
            "create_directory" => GrantCapability.CreateDirectory,
            "rename" => GrantCapability.Rename,
            "move_within_root" => GrantCapability.MoveWithinRoot,
            "copy_within_root" => GrantCapability.CopyWithinRoot,
            _ => throw new FormatException("Unknown persisted grant capability."),
        }).ToArray();
    }

    private static PersistedTeachingEpisodeStatus ParseEpisodeStatus(string value) =>
        value switch
        {
            "started" => PersistedTeachingEpisodeStatus.Started,
            "baseline_captured" => PersistedTeachingEpisodeStatus.BaselineCaptured,
            "observing_effects" => PersistedTeachingEpisodeStatus.ObservingEffects,
            "stopped" => PersistedTeachingEpisodeStatus.Stopped,
            "reconciled" => PersistedTeachingEpisodeStatus.Reconciled,
            "invalid" => PersistedTeachingEpisodeStatus.Invalid,
            _ => throw new FormatException("Unknown persisted teaching episode status."),
        };

    private static PersistedTeachingEvidenceStatus ParseEvidenceStatus(string value) =>
        value switch
        {
            "pending" => PersistedTeachingEvidenceStatus.Pending,
            "complete" => PersistedTeachingEvidenceStatus.Complete,
            "incomplete" => PersistedTeachingEvidenceStatus.Incomplete,
            "ambiguous" => PersistedTeachingEvidenceStatus.Ambiguous,
            "unsupported" => PersistedTeachingEvidenceStatus.Unsupported,
            _ => throw new FormatException("Unknown persisted teaching evidence status."),
        };

    private static PersistedExecutionStatus ParseExecutionStatus(string value) =>
        value switch
        {
            "running" => PersistedExecutionStatus.Running,
            "verified" => PersistedExecutionStatus.Verified,
            "failed" => PersistedExecutionStatus.Failed,
            "recovery_required" => PersistedExecutionStatus.RecoveryRequired,
            "cancelled" => PersistedExecutionStatus.Cancelled,
            _ => throw new FormatException("Unknown persisted execution status."),
        };
}
