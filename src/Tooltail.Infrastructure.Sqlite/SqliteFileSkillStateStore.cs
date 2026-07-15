using System.Data;
using Microsoft.Data.Sqlite;
using Tooltail.Application.Abstractions;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Domain.Teaching;

namespace Tooltail.Infrastructure.Sqlite;

public sealed partial class SqliteFileSkillStateStore : IFileSkillStateStore
{
    private readonly TooltailSqliteDatabase database;

    public SqliteFileSkillStateStore(TooltailSqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        this.database = database;
    }

    public ValueTask<StateWriteResult> StoreCompanionAsync(
        CompanionStateRecord companion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(companion);
        if (companion.Id.Value == Guid.Empty ||
            string.IsNullOrWhiteSpace(companion.DisplayName) ||
            companion.DisplayName.Length > 200 ||
            companion.CreatedUtc.Offset != TimeSpan.Zero ||
            companion.IdentitySchemaVersion < 1 ||
            !SqliteStorePrimitives.IsValidJson(companion.PresentationJson))
        {
            return ValueTask.FromResult(
                StateWriteResult.Failure("persistence.companion_invalid"));
        }

        return WriteAsync(
            async (connection, token) =>
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    "INSERT INTO companions " +
                    "(companion_id, display_name, created_utc, identity_schema_version, presentation_json) " +
                    "VALUES ($id, $name, $created, $schema, $presentation) " +
                    "ON CONFLICT(companion_id) DO UPDATE SET " +
                    "display_name = excluded.display_name, " +
                    "presentation_json = excluded.presentation_json " +
                    "WHERE companions.created_utc = excluded.created_utc " +
                    "AND companions.identity_schema_version = excluded.identity_schema_version;";
                command.Parameters.AddWithValue(
                    "$id",
                    SqliteStorePrimitives.FormatGuid(companion.Id.Value));
                command.Parameters.AddWithValue("$name", companion.DisplayName);
                command.Parameters.AddWithValue(
                    "$created",
                    SqliteStorePrimitives.FormatUtc(companion.CreatedUtc));
                command.Parameters.AddWithValue(
                    "$schema",
                    companion.IdentitySchemaVersion);
                command.Parameters.AddWithValue(
                    "$presentation",
                    companion.PresentationJson);
                if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                {
                    throw new PersistenceConflictException(
                        "persistence.companion_conflict");
                }
            },
            cancellationToken);
    }

    public ValueTask<StateWriteResult> StoreLocalFolderGrantAsync(
        LocalFolderGrantStateRecord grant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(grant.Grant);
        LocalFolderGrant value = grant.Grant;
        if (value.Id.Value == Guid.Empty ||
            value.CompanionId.Value == Guid.Empty ||
            !Enum.IsDefined(value.State) ||
            value.IssuedAt.Offset != TimeSpan.Zero ||
            (value.ExpiresAt is not null && value.ExpiresAt.Value.Offset != TimeSpan.Zero) ||
            (value.RevokedAt is not null && value.RevokedAt.Value.Offset != TimeSpan.Zero) ||
            (value.State == ResourceGrantState.Revoked) != (value.RevokedAt is not null) ||
            (value.RevokedAt is null) != (value.RevocationReason is null) ||
            (value.RevocationReason is not null &&
             !IsBoundedText(value.RevocationReason, 128)) ||
            (grant.ProtectedCanonicalRoot?.Length ?? 0) > 64 * 1024)
        {
            return ValueTask.FromResult(
                StateWriteResult.Failure("persistence.grant_invalid"));
        }

        string capabilities = SqliteStorePrimitives.CapabilitiesJson(value.Capabilities);
        string fingerprint = SqliteStorePrimitives.GrantFingerprint(value);
        return WriteAsync(
            async (connection, token) =>
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    "INSERT INTO resource_grants " +
                    "(grant_id, companion_id, resource_type, root_identity, " +
                    "canonical_root_protected, capabilities_json, issued_utc, expires_utc, " +
                    "revoked_utc, revocation_reason, grant_fingerprint) VALUES " +
                    "($id, $companion, 'local_folder', $root, $protected, $capabilities, " +
                    "$issued, $expires, $revoked, $reason, $fingerprint) " +
                    "ON CONFLICT(grant_id) DO UPDATE SET " +
                    "canonical_root_protected = excluded.canonical_root_protected, " +
                    "revoked_utc = excluded.revoked_utc, " +
                    "revocation_reason = excluded.revocation_reason " +
                    "WHERE resource_grants.companion_id = excluded.companion_id " +
                    "AND resource_grants.resource_type = excluded.resource_type " +
                    "AND resource_grants.root_identity = excluded.root_identity " +
                    "AND resource_grants.capabilities_json = excluded.capabilities_json " +
                    "AND resource_grants.issued_utc = excluded.issued_utc " +
                    "AND resource_grants.expires_utc IS excluded.expires_utc " +
                    "AND resource_grants.grant_fingerprint = excluded.grant_fingerprint " +
                    "AND (resource_grants.revoked_utc IS NULL " +
                    "OR (resource_grants.revoked_utc = excluded.revoked_utc " +
                    "AND resource_grants.revocation_reason = excluded.revocation_reason));";
                command.Parameters.AddWithValue(
                    "$id",
                    SqliteStorePrimitives.FormatGuid(value.Id.Value));
                command.Parameters.AddWithValue(
                    "$companion",
                    SqliteStorePrimitives.FormatGuid(value.CompanionId.Value));
                command.Parameters.AddWithValue("$root", value.RootIdentity.Value);
                command.Parameters.Add(
                    new SqliteParameter(
                        "$protected",
                        SqliteType.Blob)
                    {
                        Value = (object?)grant.ProtectedCanonicalRoot?.ToArray() ??
                            DBNull.Value,
                    });
                command.Parameters.AddWithValue("$capabilities", capabilities);
                command.Parameters.AddWithValue(
                    "$issued",
                    SqliteStorePrimitives.FormatUtc(value.IssuedAt));
                command.Parameters.AddWithValue(
                    "$expires",
                    value.ExpiresAt is null
                        ? DBNull.Value
                        : SqliteStorePrimitives.FormatUtc(value.ExpiresAt.Value));
                command.Parameters.AddWithValue(
                    "$revoked",
                    value.RevokedAt is null
                        ? DBNull.Value
                        : SqliteStorePrimitives.FormatUtc(value.RevokedAt.Value));
                command.Parameters.AddWithValue(
                    "$reason",
                    (object?)value.RevocationReason ?? DBNull.Value);
                command.Parameters.AddWithValue("$fingerprint", fingerprint);
                if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                {
                    throw new PersistenceConflictException(
                        "persistence.grant_conflict");
                }
            },
            cancellationToken);
    }

    public ValueTask<StateWriteResult> StoreFolderSnapshotAsync(
        FolderSnapshotStateRecord snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        bool statusHasReason = snapshot.Status != PersistedSnapshotStatus.Complete;
        if (snapshot.SnapshotId == Guid.Empty ||
            snapshot.GrantId.Value == Guid.Empty ||
            snapshot.RootIdentity is null ||
            snapshot.StartedUtc.Offset != TimeSpan.Zero ||
            snapshot.CompletedUtc.Offset != TimeSpan.Zero ||
            snapshot.CompletedUtc < snapshot.StartedUtc ||
            !Enum.IsDefined(snapshot.Status) ||
            statusHasReason != (snapshot.ReasonCode is not null) ||
            (snapshot.ReasonCode is not null && !IsReasonCode(snapshot.ReasonCode)) ||
            snapshot.HashedBytes < 0 ||
            !SqliteStorePrimitives.IsValidJson(snapshot.SnapshotJson) ||
            (snapshot.ExpiresUtc is not null &&
             (snapshot.ExpiresUtc.Value.Offset != TimeSpan.Zero ||
              snapshot.ExpiresUtc <= snapshot.CompletedUtc)))
        {
            return ValueTask.FromResult(
                StateWriteResult.Failure("persistence.snapshot_invalid"));
        }

        return WriteAsync(
            async (connection, token) =>
            {
                await RequireGrantBindingAsync(
                    connection,
                    snapshot.GrantId,
                    expectedCompanionId: null,
                    snapshot.RootIdentity,
                    token).ConfigureAwait(false);
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    "INSERT INTO folder_snapshots " +
                    "(snapshot_id, grant_id, root_identity, started_utc, completed_utc, " +
                    "status, reason_code, hashed_bytes, snapshot_json, expires_utc) VALUES " +
                    "($id, $grant, $root, $started, $completed, $status, $reason, " +
                    "$hashed, $json, $expires) " +
                    "ON CONFLICT(snapshot_id) DO UPDATE SET snapshot_id = excluded.snapshot_id " +
                    "WHERE folder_snapshots.grant_id = excluded.grant_id " +
                    "AND folder_snapshots.root_identity = excluded.root_identity " +
                    "AND folder_snapshots.started_utc = excluded.started_utc " +
                    "AND folder_snapshots.completed_utc = excluded.completed_utc " +
                    "AND folder_snapshots.status = excluded.status " +
                    "AND folder_snapshots.reason_code IS excluded.reason_code " +
                    "AND folder_snapshots.hashed_bytes = excluded.hashed_bytes " +
                    "AND folder_snapshots.snapshot_json = excluded.snapshot_json " +
                    "AND folder_snapshots.expires_utc IS excluded.expires_utc;";
                command.Parameters.AddWithValue(
                    "$id",
                    SqliteStorePrimitives.FormatGuid(snapshot.SnapshotId));
                command.Parameters.AddWithValue(
                    "$grant",
                    SqliteStorePrimitives.FormatGuid(snapshot.GrantId.Value));
                command.Parameters.AddWithValue("$root", snapshot.RootIdentity.Value);
                command.Parameters.AddWithValue(
                    "$started",
                    SqliteStorePrimitives.FormatUtc(snapshot.StartedUtc));
                command.Parameters.AddWithValue(
                    "$completed",
                    SqliteStorePrimitives.FormatUtc(snapshot.CompletedUtc));
                command.Parameters.AddWithValue("$status", ToStorage(snapshot.Status));
                command.Parameters.AddWithValue(
                    "$reason",
                    (object?)snapshot.ReasonCode ?? DBNull.Value);
                command.Parameters.AddWithValue("$hashed", snapshot.HashedBytes);
                command.Parameters.AddWithValue("$json", snapshot.SnapshotJson);
                command.Parameters.AddWithValue(
                    "$expires",
                    snapshot.ExpiresUtc is null
                        ? DBNull.Value
                        : SqliteStorePrimitives.FormatUtc(snapshot.ExpiresUtc.Value));
                if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                {
                    throw new PersistenceConflictException(
                        "persistence.snapshot_conflict");
                }
            },
            cancellationToken);
    }

    public ValueTask<StateWriteResult> StoreTeachingEpisodeAsync(
        TeachingEpisodeStateRecord episode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episode);
        ArgumentNullException.ThrowIfNull(episode.Episode);
        ArgumentNullException.ThrowIfNull(episode.Examples);
        TeachingEpisode value = episode.Episode;
        if (value.Id.Value == Guid.Empty ||
            value.CompanionId.Value == Guid.Empty ||
            value.GrantId.Value == Guid.Empty ||
            value.StartedAt.Offset != TimeSpan.Zero ||
            (value.StoppedAt is not null && value.StoppedAt.Value.Offset != TimeSpan.Zero) ||
            !Enum.IsDefined(value.State) ||
            !Enum.IsDefined(value.EvidenceState) ||
            (value.InvalidReasonCode is not null &&
             !IsReasonCode(value.InvalidReasonCode)) ||
            !SqliteStorePrimitives.IsValidJson(
                episode.ReconciliationSummaryJson,
                nullable: true) ||
            (episode.RawEvidenceExpiryUtc is not null &&
             (episode.RawEvidenceExpiryUtc.Value.Offset != TimeSpan.Zero ||
              episode.RawEvidenceExpiryUtc < value.StartedAt)) ||
            episode.Examples.Count > 10_000 ||
            (episode.Examples.Count > 0 &&
             (value.State != TeachingEpisodeState.Reconciled ||
              value.EvidenceState != TeachingEvidenceState.Complete)) ||
            episode.Examples.Any(static example => !IsValidExample(example)))
        {
            return ValueTask.FromResult(
                StateWriteResult.Failure("persistence.episode_invalid"));
        }

        return WriteAsync(
            async (connection, token) =>
            {
                await RequireGrantBindingAsync(
                    connection,
                    value.GrantId,
                    value.CompanionId,
                    expectedRootIdentity: null,
                    token).ConfigureAwait(false);
                string? currentState = await ReadEpisodeStateAsync(
                    connection,
                    value.Id,
                    value.CompanionId,
                    value.GrantId,
                    value.StartedAt,
                    token).ConfigureAwait(false);
                string nextState = SqliteStorePrimitives.ToStorage(value.State);
                if (currentState is not null &&
                    !EpisodeTransitionAllowed(currentState, nextState))
                {
                    throw new PersistenceConflictException(
                        "persistence.episode_state_regressed");
                }

                await UpsertEpisodeAsync(connection, episode, token).ConfigureAwait(false);
                foreach (DemonstrationExampleStateRecord example in episode.Examples)
                {
                    await InsertExampleAsync(connection, value.Id, example, token)
                        .ConfigureAwait(false);
                }
            },
            cancellationToken);
    }

    public ValueTask<StateWriteResult> StoreSkillVersionAsync(
        SkillVersionStateRecord skillVersion,
        CancellationToken cancellationToken = default) =>
        StoreSkillVersionCoreAsync(skillVersion, cancellationToken);

    public ValueTask<StateWriteResult> StoreExecutionPlanAsync(
        ExecutionPlan plan,
        string canonicalJson,
        CancellationToken cancellationToken = default) =>
        StorePlanAsync(plan, canonicalJson, cancellationToken);

    public ValueTask<StateWriteResult> StoreRecoveryPlanAsync(
        RecoveryPlan plan,
        string canonicalJson,
        CancellationToken cancellationToken = default) =>
        StorePlanAsync(plan, canonicalJson, cancellationToken);

    public ValueTask<StateWriteResult> StoreApprovalAsync(
        PlanApproval approval,
        CancellationToken cancellationToken = default) =>
        StoreApprovalCoreAsync(approval, cancellationToken);

    public ValueTask<StateReadResult<SkillVersionStateRecord>> LoadSkillVersionAsync(
        SkillId skillId,
        SkillVersionNumber version,
        CancellationToken cancellationToken = default) =>
        LoadSkillVersionCoreAsync(skillId, version, cancellationToken);

    public ValueTask<StateReadResult<StoredPlanDocument>> LoadPlanDocumentAsync(
        PlanId planId,
        CancellationToken cancellationToken = default) =>
        LoadPlanDocumentCoreAsync(planId, cancellationToken);

    private async ValueTask<StateWriteResult> WriteAsync(
        Func<SqliteConnection, CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        IDisposable? writer = null;
        SqliteConnection? connection = null;
        bool transactionActive = false;
        try
        {
            writer = await database.AcquireWriterAsync(cancellationToken)
                .ConfigureAwait(false);
            connection = await database.OpenReadWriteAsync(cancellationToken)
                .ConfigureAwait(false);
            await SqliteStorePrimitives.BeginImmediateAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            transactionActive = true;
            await action(connection, cancellationToken).ConfigureAwait(false);
            await SqliteStorePrimitives.CommitAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            transactionActive = false;
            return StateWriteResult.Success;
        }
        catch (OperationCanceledException)
        {
            if (transactionActive && connection is not null)
            {
                await SqliteStorePrimitives.RollbackAsync(connection).ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception exception) when (IsExpectedStoreFailure(exception))
        {
            if (transactionActive && connection is not null)
            {
                await SqliteStorePrimitives.RollbackAsync(connection).ConfigureAwait(false);
            }

            return StateWriteResult.Failure(
                SqliteStorePrimitives.MapFailure(exception));
        }
        finally
        {
            try
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                writer?.Dispose();
            }
        }
    }

    private static async Task<string?> ReadEpisodeStateAsync(
        SqliteConnection connection,
        TeachingEpisodeId episodeId,
        CompanionId companionId,
        GrantId grantId,
        DateTimeOffset startedUtc,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT companion_id, grant_id, started_utc, status " +
            "FROM teaching_episodes WHERE episode_id = $id;";
        command.Parameters.AddWithValue(
            "$id",
            SqliteStorePrimitives.FormatGuid(episodeId.Value));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (!string.Equals(
                reader.GetString(0),
                SqliteStorePrimitives.FormatGuid(companionId.Value),
                StringComparison.Ordinal) ||
            !string.Equals(
                reader.GetString(1),
                SqliteStorePrimitives.FormatGuid(grantId.Value),
                StringComparison.Ordinal) ||
            !string.Equals(
                reader.GetString(2),
                SqliteStorePrimitives.FormatUtc(startedUtc),
                StringComparison.Ordinal))
        {
            throw new PersistenceConflictException(
                "persistence.episode_identity_conflict");
        }

        return reader.GetString(3);
    }

    private static async Task RequireGrantBindingAsync(
        SqliteConnection connection,
        GrantId grantId,
        CompanionId? expectedCompanionId,
        ResourceRootIdentity? expectedRootIdentity,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT companion_id, root_identity FROM resource_grants " +
            "WHERE grant_id = $grant AND resource_type = 'local_folder';";
        command.Parameters.AddWithValue(
            "$grant",
            SqliteStorePrimitives.FormatGuid(grantId.Value));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            (expectedCompanionId is not null &&
             !string.Equals(
                 reader.GetString(0),
                 SqliteStorePrimitives.FormatGuid(expectedCompanionId.Value.Value),
                 StringComparison.Ordinal)) ||
            (expectedRootIdentity is not null &&
             !string.Equals(
                 reader.GetString(1),
                 expectedRootIdentity.Value,
                 StringComparison.Ordinal)))
        {
            throw new PersistenceConflictException(
                "persistence.grant_binding_mismatch");
        }
    }

    private static async Task UpsertEpisodeAsync(
        SqliteConnection connection,
        TeachingEpisodeStateRecord record,
        CancellationToken cancellationToken)
    {
        TeachingEpisode episode = record.Episode;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO teaching_episodes " +
            "(episode_id, companion_id, grant_id, started_utc, stopped_utc, status, " +
            "evidence_status, baseline_snapshot_ref, final_snapshot_ref, " +
            "reconciliation_summary_json, invalid_reason, raw_evidence_expiry_utc) VALUES " +
            "($id, $companion, $grant, $started, $stopped, $status, $evidence, " +
            "$baseline, $final, $summary, $invalid, $expiry) " +
            "ON CONFLICT(episode_id) DO UPDATE SET " +
            "stopped_utc = excluded.stopped_utc, status = excluded.status, " +
            "evidence_status = excluded.evidence_status, " +
            "baseline_snapshot_ref = COALESCE(excluded.baseline_snapshot_ref, teaching_episodes.baseline_snapshot_ref), " +
            "final_snapshot_ref = COALESCE(excluded.final_snapshot_ref, teaching_episodes.final_snapshot_ref), " +
            "reconciliation_summary_json = COALESCE(excluded.reconciliation_summary_json, teaching_episodes.reconciliation_summary_json), " +
            "invalid_reason = excluded.invalid_reason, " +
            "raw_evidence_expiry_utc = COALESCE(excluded.raw_evidence_expiry_utc, teaching_episodes.raw_evidence_expiry_utc);";
        command.Parameters.AddWithValue(
            "$id",
            SqliteStorePrimitives.FormatGuid(episode.Id.Value));
        command.Parameters.AddWithValue(
            "$companion",
            SqliteStorePrimitives.FormatGuid(episode.CompanionId.Value));
        command.Parameters.AddWithValue(
            "$grant",
            SqliteStorePrimitives.FormatGuid(episode.GrantId.Value));
        command.Parameters.AddWithValue(
            "$started",
            SqliteStorePrimitives.FormatUtc(episode.StartedAt));
        command.Parameters.AddWithValue(
            "$stopped",
            episode.StoppedAt is null
                ? DBNull.Value
                : SqliteStorePrimitives.FormatUtc(episode.StoppedAt.Value));
        command.Parameters.AddWithValue(
            "$status",
            SqliteStorePrimitives.ToStorage(episode.State));
        command.Parameters.AddWithValue(
            "$evidence",
            SqliteStorePrimitives.ToStorage(episode.EvidenceState));
        command.Parameters.AddWithValue(
            "$baseline",
            record.BaselineSnapshotId is null
                ? DBNull.Value
                : SqliteStorePrimitives.FormatGuid(record.BaselineSnapshotId.Value));
        command.Parameters.AddWithValue(
            "$final",
            record.FinalSnapshotId is null
                ? DBNull.Value
                : SqliteStorePrimitives.FormatGuid(record.FinalSnapshotId.Value));
        command.Parameters.AddWithValue(
            "$summary",
            (object?)record.ReconciliationSummaryJson ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$invalid",
            (object?)episode.InvalidReasonCode ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$expiry",
            record.RawEvidenceExpiryUtc is null
                ? DBNull.Value
                : SqliteStorePrimitives.FormatUtc(record.RawEvidenceExpiryUtc.Value));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new PersistenceConflictException("persistence.episode_conflict");
        }
    }

    private static async Task InsertExampleAsync(
        SqliteConnection connection,
        TeachingEpisodeId episodeId,
        DemonstrationExampleStateRecord example,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO demonstration_examples " +
            "(example_id, episode_id, effect_type, source_relative_path, " +
            "destination_relative_path, source_fingerprint_json, user_label) VALUES " +
            "($id, $episode, $effect, $source, $destination, $fingerprint, $label) " +
            "ON CONFLICT(example_id) DO UPDATE SET example_id = excluded.example_id " +
            "WHERE demonstration_examples.episode_id = excluded.episode_id " +
            "AND demonstration_examples.effect_type = excluded.effect_type " +
            "AND demonstration_examples.source_relative_path IS excluded.source_relative_path " +
            "AND demonstration_examples.destination_relative_path = excluded.destination_relative_path " +
            "AND demonstration_examples.source_fingerprint_json IS excluded.source_fingerprint_json " +
            "AND demonstration_examples.user_label IS excluded.user_label;";
        command.Parameters.AddWithValue(
            "$id",
            SqliteStorePrimitives.FormatGuid(example.Id.Value));
        command.Parameters.AddWithValue(
            "$episode",
            SqliteStorePrimitives.FormatGuid(episodeId.Value));
        command.Parameters.AddWithValue(
            "$effect",
            SqliteStorePrimitives.ToStorage(example.EffectType));
        command.Parameters.AddWithValue(
            "$source",
            (object?)example.SourceRelativePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$destination", example.DestinationRelativePath);
        command.Parameters.AddWithValue(
            "$fingerprint",
            (object?)example.SourceFingerprintJson ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$label",
            (object?)example.UserLabel ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new PersistenceConflictException("persistence.example_conflict");
        }
    }

    private static bool IsValidExample(DemonstrationExampleStateRecord example)
    {
        if (example is null ||
            example.Id.Value == Guid.Empty ||
            !Enum.IsDefined(example.EffectType) ||
            string.IsNullOrWhiteSpace(example.DestinationRelativePath) ||
            example.DestinationRelativePath.Length > 1024 ||
            (example.UserLabel is not null &&
             (!IsBoundedText(example.UserLabel, 200))) ||
            !SqliteStorePrimitives.IsValidJson(
                example.SourceFingerprintJson,
                nullable: true))
        {
            return false;
        }

        bool directory = example.EffectType == FilePrimitive.EnsureDirectory;
        return directory
            ? example.SourceRelativePath is null &&
              example.SourceFingerprintJson is null
            : !string.IsNullOrWhiteSpace(example.SourceRelativePath) &&
              example.SourceRelativePath.Length <= 1024 &&
              example.SourceFingerprintJson is not null;
    }

    private static bool EpisodeTransitionAllowed(string current, string next) =>
        current == next ||
        (current, next) is
            ("started", "baseline_captured") or
            ("started", "invalid") or
            ("baseline_captured", "observing_effects") or
            ("baseline_captured", "invalid") or
            ("observing_effects", "stopped") or
            ("observing_effects", "invalid") or
            ("stopped", "reconciled") or
            ("stopped", "invalid");

    private static string ToStorage(PersistedSnapshotStatus status) =>
        status switch
        {
            PersistedSnapshotStatus.Complete => "complete",
            PersistedSnapshotStatus.Incomplete => "incomplete",
            PersistedSnapshotStatus.Invalid => "invalid",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

    private static bool IsReasonCode(string value) =>
        value.Length is > 0 and <= 128 &&
        value.All(static character =>
            character is >= 'a' and <= 'z' or
                >= '0' and <= '9' or '.' or '_' or '-');

    private static bool IsBoundedText(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    private static bool IsExpectedStoreFailure(Exception exception) =>
        exception is PersistenceConflictException or SqliteException or IOException or
            UnauthorizedAccessException or System.Security.SecurityException or
            InvalidOperationException or ArgumentException or FormatException;

    private partial ValueTask<StateWriteResult> StoreSkillVersionCoreAsync(
        SkillVersionStateRecord skillVersion,
        CancellationToken cancellationToken);

    private partial ValueTask<StateWriteResult> StorePlanAsync(
        ExecutionPlan plan,
        string canonicalJson,
        CancellationToken cancellationToken);

    private partial ValueTask<StateWriteResult> StorePlanAsync(
        RecoveryPlan plan,
        string canonicalJson,
        CancellationToken cancellationToken);

    private partial ValueTask<StateWriteResult> StoreApprovalCoreAsync(
        PlanApproval approval,
        CancellationToken cancellationToken);

    private partial ValueTask<StateReadResult<SkillVersionStateRecord>>
        LoadSkillVersionCoreAsync(
            SkillId skillId,
            SkillVersionNumber version,
            CancellationToken cancellationToken);

    private partial ValueTask<StateReadResult<StoredPlanDocument>>
        LoadPlanDocumentCoreAsync(
            PlanId planId,
            CancellationToken cancellationToken);
}
