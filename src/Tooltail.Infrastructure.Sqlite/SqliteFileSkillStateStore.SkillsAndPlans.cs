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
    private partial ValueTask<StateWriteResult> StoreSkillVersionCoreAsync(
        SkillVersionStateRecord skillVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(skillVersion);
        ArgumentNullException.ThrowIfNull(skillVersion.Version);
        SkillVersion version = skillVersion.Version;
        bool lifecycleNeedsApproval = version.Lifecycle is
            SkillLifecycleState.Approved or
            SkillLifecycleState.Practiced or
            SkillLifecycleState.Reliable or
            SkillLifecycleState.Delegated;
        if (skillVersion.CompanionId.Value == Guid.Empty ||
            version.SkillId.Value == Guid.Empty ||
            !IsBoundedText(skillVersion.DisplayName, 200) ||
            skillVersion.SkillCreatedUtc.Offset != TimeSpan.Zero ||
            version.CreatedAt.Offset != TimeSpan.Zero ||
            version.CreatedAt < skillVersion.SkillCreatedUtc ||
            !IsBoundedText(skillVersion.SchemaVersion, 64) ||
            !SqliteStorePrimitives.IsValidJson(skillVersion.SkillSpecJson) ||
            !SqliteStorePrimitives.HashMatches(
                skillVersion.SkillSpecJson,
                version.SpecificationHash) ||
            !IsBoundedText(skillVersion.CompilerId, 128) ||
            !IsBoundedText(version.CompilerVersion, 64) ||
            !IsBoundedText(version.MinimumExecutorVersion, 64) ||
            !Enum.IsDefined(version.Lifecycle) ||
            (skillVersion.ApprovedUtc is not null &&
             (skillVersion.ApprovedUtc.Value.Offset != TimeSpan.Zero ||
              skillVersion.ApprovedUtc < version.CreatedAt)) ||
            (lifecycleNeedsApproval && skillVersion.ApprovedUtc is null) ||
            (version.Lifecycle == SkillLifecycleState.Draft &&
             skillVersion.ApprovedUtc is not null) ||
            !SqliteStorePrimitives.IsValidJson(
                skillVersion.SemanticDiffJson,
                nullable: true))
        {
            return ValueTask.FromResult(
                StateWriteResult.Failure("persistence.skill_version_invalid"));
        }

        return WriteAsync(
            async (connection, token) =>
            {
                await UpsertSkillAsync(connection, skillVersion, token)
                    .ConfigureAwait(false);
                string versionId = SqliteStorePrimitives.SkillVersionKey(
                    version.SkillId,
                    version.Number);
                PersistedSkillVersion? existing = await ReadPersistedSkillVersionAsync(
                    connection,
                    versionId,
                    token).ConfigureAwait(false);
                if (existing is null)
                {
                    await InsertSkillVersionAsync(connection, skillVersion, versionId, token)
                        .ConfigureAwait(false);
                }
                else
                {
                    ValidateExistingSkillVersion(existing, skillVersion, versionId);
                    await UpdateSkillProjectionAsync(
                        connection,
                        skillVersion,
                        versionId,
                        token).ConfigureAwait(false);
                }

                if (skillVersion.MakeCurrent)
                {
                    await AdvanceCurrentSkillVersionAsync(
                        connection,
                        version.SkillId,
                        version.Number,
                        versionId,
                        token).ConfigureAwait(false);
                }
            },
            cancellationToken);
    }

    private partial ValueTask<StateWriteResult> StorePlanAsync(
        ExecutionPlan plan,
        string canonicalJson,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ExecutionPlanDefinition definition = plan.Definition;
        if (!IsCanonicalPlanDocument(
                canonicalJson,
                "tooltail.execution-plan/1",
                plan.Fingerprint.Value,
                definition.Id,
                definition.SkillId,
                definition.SkillVersion,
                definition.GrantId,
                definition.CreatedUtc,
                definition.ExpiresUtc,
                originalExecutionId: null,
                originalPlanId: null,
                originalPlanFingerprint: null) ||
            !PlanDocumentMatchesDefinition(canonicalJson, definition))
        {
            return ValueTask.FromResult(
                StateWriteResult.Failure("persistence.plan_document_invalid"));
        }

        return StorePlanDocumentAsync(
            definition.Id,
            PersistedPlanKind.Standard,
            definition.SkillId,
            definition.SkillVersion,
            definition.GrantId,
            definition.SkillSpecificationHash,
            definition.RootIdentity,
            definition.GrantedCapabilities,
            plan.Fingerprint,
            definition.CreatedUtc,
            definition.ExpiresUtc,
            "tooltail.execution-plan/1",
            canonicalJson,
            originalExecutionId: null,
            originalPlanId: null,
            originalPlanFingerprint: null,
            cancellationToken);
    }

    private partial ValueTask<StateWriteResult> StorePlanAsync(
        RecoveryPlan plan,
        string canonicalJson,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        RecoveryPlanDefinition definition = plan.Definition;
        if (!IsCanonicalPlanDocument(
                canonicalJson,
                "tooltail.recovery-plan/1",
                plan.Fingerprint.Value,
                definition.Id,
                definition.SkillId,
                definition.SkillVersion,
                definition.GrantId,
                definition.CreatedUtc,
                definition.ExpiresUtc,
                definition.OriginalExecutionId,
                definition.OriginalPlanId,
                definition.OriginalPlanFingerprint) ||
            !PlanDocumentMatchesDefinition(canonicalJson, definition))
        {
            return ValueTask.FromResult(
                StateWriteResult.Failure("persistence.plan_document_invalid"));
        }

        return StorePlanDocumentAsync(
            definition.Id,
            PersistedPlanKind.Recovery,
            definition.SkillId,
            definition.SkillVersion,
            definition.GrantId,
            definition.SkillSpecificationHash,
            definition.RootIdentity,
            definition.GrantedCapabilities,
            plan.Fingerprint,
            definition.CreatedUtc,
            definition.ExpiresUtc,
            "tooltail.recovery-plan/1",
            canonicalJson,
            definition.OriginalExecutionId,
            definition.OriginalPlanId,
            definition.OriginalPlanFingerprint,
            cancellationToken);
    }

    private partial ValueTask<StateWriteResult> StoreApprovalCoreAsync(
        PlanApproval approval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);
        bool stateShapeValid = approval.State switch
        {
            PlanApprovalState.Active =>
                approval.ConsumedUtc is null &&
                approval.RevokedUtc is null &&
                approval.RevocationReason is null,
            PlanApprovalState.Consumed =>
                approval.ConsumedUtc is not null &&
                approval.RevokedUtc is null &&
                approval.RevocationReason is null,
            PlanApprovalState.Revoked =>
                approval.ConsumedUtc is null &&
                approval.RevokedUtc is not null &&
                approval.RevocationReason is not null,
            _ => false,
        };
        if (approval.Id.Value == Guid.Empty ||
            approval.PlanId.Value == Guid.Empty ||
            approval.ApprovedUtc.Offset != TimeSpan.Zero ||
            approval.ExpiresUtc.Offset != TimeSpan.Zero ||
            approval.ExpiresUtc <= approval.ApprovedUtc ||
            !Enum.IsDefined(approval.Purpose) ||
            !stateShapeValid ||
            (approval.ConsumedUtc is not null &&
             (approval.ConsumedUtc.Value.Offset != TimeSpan.Zero ||
              approval.ConsumedUtc < approval.ApprovedUtc ||
              approval.ConsumedUtc >= approval.ExpiresUtc)) ||
            (approval.RevokedUtc is not null &&
             (approval.RevokedUtc.Value.Offset != TimeSpan.Zero ||
              approval.RevokedUtc < approval.ApprovedUtc)) ||
            (approval.RevocationReason is not null &&
             !IsBoundedText(approval.RevocationReason, 128)))
        {
            return ValueTask.FromResult(
                StateWriteResult.Failure("persistence.approval_invalid"));
        }

        return WriteAsync(
            async (connection, token) =>
            {
                await ValidateApprovalPlanAsync(connection, approval, token)
                    .ConfigureAwait(false);
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    "INSERT INTO approvals " +
                    "(approval_id, plan_id, plan_fingerprint, approved_utc, expires_utc, " +
                    "approval_purpose, consumed_utc, revoked_utc, revocation_reason) VALUES " +
                    "($id, $plan, $fingerprint, $approved, $expires, $purpose, " +
                    "$consumed, $revoked, $reason) " +
                    "ON CONFLICT(approval_id) DO UPDATE SET " +
                    "consumed_utc = excluded.consumed_utc, " +
                    "revoked_utc = excluded.revoked_utc, " +
                    "revocation_reason = excluded.revocation_reason " +
                    "WHERE approvals.plan_id = excluded.plan_id " +
                    "AND approvals.plan_fingerprint = excluded.plan_fingerprint " +
                    "AND approvals.approved_utc = excluded.approved_utc " +
                    "AND approvals.expires_utc = excluded.expires_utc " +
                    "AND approvals.approval_purpose = excluded.approval_purpose " +
                    "AND ((approvals.consumed_utc IS NULL AND approvals.revoked_utc IS NULL) " +
                    "OR (approvals.consumed_utc IS excluded.consumed_utc " +
                    "AND approvals.revoked_utc IS excluded.revoked_utc " +
                    "AND approvals.revocation_reason IS excluded.revocation_reason));";
                command.Parameters.AddWithValue(
                    "$id",
                    SqliteStorePrimitives.FormatGuid(approval.Id.Value));
                command.Parameters.AddWithValue(
                    "$plan",
                    SqliteStorePrimitives.FormatGuid(approval.PlanId.Value));
                command.Parameters.AddWithValue("$fingerprint", approval.Fingerprint.Value);
                command.Parameters.AddWithValue(
                    "$approved",
                    SqliteStorePrimitives.FormatUtc(approval.ApprovedUtc));
                command.Parameters.AddWithValue(
                    "$expires",
                    SqliteStorePrimitives.FormatUtc(approval.ExpiresUtc));
                command.Parameters.AddWithValue(
                    "$purpose",
                    SqliteStorePrimitives.ToStorage(approval.Purpose));
                command.Parameters.AddWithValue(
                    "$consumed",
                    approval.ConsumedUtc is null
                        ? DBNull.Value
                        : SqliteStorePrimitives.FormatUtc(approval.ConsumedUtc.Value));
                command.Parameters.AddWithValue(
                    "$revoked",
                    approval.RevokedUtc is null
                        ? DBNull.Value
                        : SqliteStorePrimitives.FormatUtc(approval.RevokedUtc.Value));
                command.Parameters.AddWithValue(
                    "$reason",
                    (object?)approval.RevocationReason ?? DBNull.Value);
                if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                {
                    throw new PersistenceConflictException(
                        "persistence.approval_conflict");
                }

                await using SqliteCommand updatePlan = connection.CreateCommand();
                updatePlan.CommandText =
                    "UPDATE execution_plans SET status = CASE " +
                    "WHEN $consumed IS NOT NULL THEN 'consumed' " +
                    "WHEN status = 'planned' THEN 'approved' ELSE status END " +
                    "WHERE plan_id = $plan AND plan_fingerprint = $fingerprint;";
                updatePlan.Parameters.AddWithValue(
                    "$consumed",
                    approval.ConsumedUtc is null
                        ? DBNull.Value
                        : SqliteStorePrimitives.FormatUtc(approval.ConsumedUtc.Value));
                updatePlan.Parameters.AddWithValue(
                    "$plan",
                    SqliteStorePrimitives.FormatGuid(approval.PlanId.Value));
                updatePlan.Parameters.AddWithValue(
                    "$fingerprint",
                    approval.Fingerprint.Value);
                if (await updatePlan.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                {
                    throw new PersistenceConflictException(
                        "persistence.approval_plan_missing");
                }
            },
            cancellationToken);
    }

    private static async Task ValidateApprovalPlanAsync(
        SqliteConnection connection,
        PlanApproval approval,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT p.plan_kind, p.plan_fingerprint, p.created_utc, p.expires_utc, " +
            "p.status, p.plan_json, v.spec_hash, g.root_identity, " +
            "g.capabilities_json, s.companion_id, g.companion_id, " +
            "p.plan_contract_version FROM execution_plans p " +
            "JOIN skill_versions v ON v.skill_version_id = p.skill_version_id " +
            "JOIN skills s ON s.skill_id = v.skill_id " +
            "JOIN resource_grants g ON g.grant_id = p.grant_id " +
            "WHERE p.plan_id = $plan;";
        command.Parameters.AddWithValue(
            "$plan",
            SqliteStorePrimitives.FormatGuid(approval.PlanId.Value));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new PersistenceConflictException(
                "persistence.approval_plan_missing");
        }

        string planKind = reader.GetString(0);
        DateTimeOffset planCreatedUtc = SqliteStorePrimitives.ParseUtc(
            reader.GetString(2));
        DateTimeOffset planExpiresUtc = SqliteStorePrimitives.ParseUtc(
            reader.GetString(3));
        string status = reader.GetString(4);
        bool purposeMatches = approval.Purpose switch
        {
            PlanApprovalPurpose.Production or PlanApprovalPurpose.Rehearsal =>
                string.Equals(planKind, "standard", StringComparison.Ordinal),
            PlanApprovalPurpose.Undo =>
                string.Equals(planKind, "recovery", StringComparison.Ordinal),
            _ => false,
        };
        bool statusMatches = approval.State == PlanApprovalState.Consumed
            ? string.Equals(status, "consumed", StringComparison.Ordinal)
            : status is "planned" or "approved";
        string expectedContract = string.Equals(
            planKind,
            "standard",
            StringComparison.Ordinal)
                ? "tooltail.execution-plan/1"
                : "tooltail.recovery-plan/1";
        if (!purposeMatches ||
            !statusMatches ||
            !string.Equals(
                reader.GetString(1),
                approval.Fingerprint.Value,
                StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(9), reader.GetString(10), StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(11), expectedContract, StringComparison.Ordinal) ||
            !SqliteStorePrimitives.IsValidJson(reader.GetString(5)) ||
            !SqliteStorePrimitives.HashMatches(
                reader.GetString(5),
                approval.Fingerprint.Value) ||
            !PlanDocumentMatchesStoredAuthority(
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8)) ||
            approval.ApprovedUtc < planCreatedUtc ||
            approval.ApprovedUtc >= planExpiresUtc ||
            approval.ExpiresUtc > planExpiresUtc)
        {
            throw new PersistenceConflictException(
                "persistence.approval_plan_mismatch");
        }
    }

    private partial async ValueTask<StateReadResult<SkillVersionStateRecord>>
        LoadSkillVersionCoreAsync(
            SkillId skillId,
            SkillVersionNumber version,
            CancellationToken cancellationToken)
    {
        try
        {
            await using SqliteConnection connection = await database.OpenReadOnlyAsync(
                cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT s.companion_id, s.display_name, s.created_utc, s.current_version_id, " +
                "v.skill_version_id, v.parent_version_id, parent.version_number, " +
                "v.schema_version, v.skill_spec_json, v.spec_hash, v.compiler_id, " +
                "v.compiler_version, v.executor_compatibility, v.lifecycle_state, " +
                "v.created_utc, v.approved_utc, v.semantic_diff_json " +
                "FROM skill_versions v JOIN skills s ON s.skill_id = v.skill_id " +
                "LEFT JOIN skill_versions parent ON parent.skill_version_id = v.parent_version_id " +
                "WHERE v.skill_id = $skill AND v.version_number = $version;";
            command.Parameters.AddWithValue(
                "$skill",
                SqliteStorePrimitives.FormatGuid(skillId.Value));
            command.Parameters.AddWithValue("$version", version.Value);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(
                CommandBehavior.SingleRow,
                cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return StateReadResult.Failure<SkillVersionStateRecord>(
                    "persistence.skill_version_not_found");
            }

            string versionId = reader.GetString(4);
            DateTimeOffset createdUtc = SqliteStorePrimitives.ParseUtc(
                reader.GetString(14));
            DateTimeOffset? approvedUtc = reader.IsDBNull(15)
                ? null
                : SqliteStorePrimitives.ParseUtc(reader.GetString(15));
            Tooltail.Domain.Common.DomainResult<SkillVersion> restoredResult =
                SkillVersion.Rehydrate(
                skillId,
                version,
                reader.IsDBNull(6)
                    ? null
                    : new SkillVersionNumber(reader.GetInt32(6)),
                reader.GetString(9),
                reader.GetString(11),
                reader.GetString(12),
                SqliteStorePrimitives.ParseSkillLifecycle(reader.GetString(13)),
                createdUtc,
                approvedUtc is not null);
            if (!restoredResult.IsSuccess ||
                (approvedUtc is not null && approvedUtc < createdUtc))
            {
                return StateReadResult.Failure<SkillVersionStateRecord>(
                    "persistence.skill_version_corrupt");
            }

            SkillVersion restored = restoredResult.Value!;
            SkillVersionStateRecord result = new(
                new CompanionId(Guid.Parse(reader.GetString(0))),
                reader.GetString(1),
                SqliteStorePrimitives.ParseUtc(reader.GetString(2)),
                restored,
                !reader.IsDBNull(3) &&
                    string.Equals(
                        reader.GetString(3),
                        versionId,
                        StringComparison.Ordinal),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(10),
                approvedUtc,
                reader.IsDBNull(16) ? null : reader.GetString(16));
            if (!SqliteStorePrimitives.IsValidJson(result.SkillSpecJson) ||
                !SqliteStorePrimitives.HashMatches(
                    result.SkillSpecJson,
                    restored.SpecificationHash) ||
                !SqliteStorePrimitives.IsValidJson(
                    result.SemanticDiffJson,
                    nullable: true))
            {
                return StateReadResult.Failure<SkillVersionStateRecord>(
                    "persistence.skill_version_corrupt");
            }

            return StateReadResult.Success(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedStoreFailure(exception))
        {
            return StateReadResult.Failure<SkillVersionStateRecord>(
                SqliteStorePrimitives.MapFailure(exception));
        }
    }

    private partial async ValueTask<StateReadResult<StoredPlanDocument>>
        LoadPlanDocumentCoreAsync(
            PlanId planId,
            CancellationToken cancellationToken)
    {
        try
        {
            await using SqliteConnection connection = await database.OpenReadOnlyAsync(
                cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT p.plan_kind, v.skill_id, v.version_number, p.grant_id, " +
                "p.plan_fingerprint, p.created_utc, p.expires_utc, " +
                "p.plan_contract_version, p.plan_json, p.original_execution_id, " +
                "p.original_plan_id, p.original_plan_fingerprint, v.spec_hash, " +
                "g.root_identity, g.capabilities_json, s.companion_id, g.companion_id " +
                "FROM execution_plans p " +
                "JOIN skill_versions v ON v.skill_version_id = p.skill_version_id " +
                "JOIN skills s ON s.skill_id = v.skill_id " +
                "JOIN resource_grants g ON g.grant_id = p.grant_id " +
                "WHERE p.plan_id = $plan;";
            command.Parameters.AddWithValue(
                "$plan",
                SqliteStorePrimitives.FormatGuid(planId.Value));
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(
                CommandBehavior.SingleRow,
                cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return StateReadResult.Failure<StoredPlanDocument>(
                    "persistence.plan_not_found");
            }

            PersistedPlanKind kind = reader.GetString(0) switch
            {
                "standard" => PersistedPlanKind.Standard,
                "recovery" => PersistedPlanKind.Recovery,
                _ => throw new FormatException("Unknown persisted plan kind."),
            };
            StoredPlanDocument result = new(
                planId,
                kind,
                new SkillId(Guid.Parse(reader.GetString(1))),
                new SkillVersionNumber(reader.GetInt32(2)),
                new GrantId(Guid.Parse(reader.GetString(3))),
                new PlanFingerprint(reader.GetString(4)),
                SqliteStorePrimitives.ParseUtc(reader.GetString(5)),
                SqliteStorePrimitives.ParseUtc(reader.GetString(6)),
                reader.GetString(7),
                reader.GetString(8));
            ExecutionId? originalExecutionId = reader.IsDBNull(9)
                ? null
                : new ExecutionId(Guid.Parse(reader.GetString(9)));
            PlanId? originalPlanId = reader.IsDBNull(10)
                ? null
                : new PlanId(Guid.Parse(reader.GetString(10)));
            PlanFingerprint? originalPlanFingerprint = reader.IsDBNull(11)
                ? null
                : new PlanFingerprint(reader.GetString(11));
            string expectedContract = kind == PersistedPlanKind.Standard
                ? "tooltail.execution-plan/1"
                : "tooltail.recovery-plan/1";
            if (!string.Equals(
                    result.ContractVersion,
                    expectedContract,
                    StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(15), reader.GetString(16), StringComparison.Ordinal) ||
                !PlanDocumentMatchesStoredAuthority(
                    result.CanonicalJson,
                    reader.GetString(12),
                    reader.GetString(13),
                    reader.GetString(14)) ||
                !IsCanonicalPlanDocument(
                    result.CanonicalJson,
                    result.ContractVersion,
                    result.Fingerprint.Value,
                    result.Id,
                    result.SkillId,
                    result.SkillVersion,
                    result.GrantId,
                    result.CreatedUtc,
                    result.ExpiresUtc,
                    originalExecutionId,
                    originalPlanId,
                    originalPlanFingerprint))
            {
                return StateReadResult.Failure<StoredPlanDocument>(
                    "persistence.plan_corrupt");
            }

            return StateReadResult.Success(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedStoreFailure(exception))
        {
            return StateReadResult.Failure<StoredPlanDocument>(
                SqliteStorePrimitives.MapFailure(exception));
        }
    }

    private ValueTask<StateWriteResult> StorePlanDocumentAsync(
        PlanId planId,
        PersistedPlanKind kind,
        SkillId skillId,
        SkillVersionNumber skillVersion,
        GrantId grantId,
        SkillSpecificationHash specificationHash,
        ResourceRootIdentity rootIdentity,
        IEnumerable<GrantCapability> grantedCapabilities,
        PlanFingerprint fingerprint,
        DateTimeOffset createdUtc,
        DateTimeOffset expiresUtc,
        string contractVersion,
        string canonicalJson,
        ExecutionId? originalExecutionId,
        PlanId? originalPlanId,
        PlanFingerprint? originalPlanFingerprint,
        CancellationToken cancellationToken)
    {
        if (createdUtc.Offset != TimeSpan.Zero ||
            expiresUtc.Offset != TimeSpan.Zero ||
            expiresUtc <= createdUtc)
        {
            return ValueTask.FromResult(
                StateWriteResult.Failure("persistence.plan_time_invalid"));
        }

        string capabilitiesJson = SqliteStorePrimitives.CapabilitiesJson(
            grantedCapabilities);
        return WriteAsync(
            async (connection, token) =>
            {
                await ValidatePlanPrerequisitesAsync(
                    connection,
                    skillId,
                    skillVersion,
                    grantId,
                    specificationHash.Value,
                    rootIdentity.Value,
                    capabilitiesJson,
                    createdUtc,
                    token).ConfigureAwait(false);
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    "INSERT INTO execution_plans " +
                    "(plan_id, plan_kind, skill_version_id, grant_id, original_execution_id, " +
                    "original_plan_id, original_plan_fingerprint, plan_contract_version, " +
                    "created_utc, plan_json, plan_fingerprint, status, expires_utc) VALUES " +
                    "($id, $kind, $skill_version, $grant, $original_execution, " +
                    "$original_plan, $original_fingerprint, $contract, $created, $json, " +
                    "$fingerprint, 'planned', $expires) " +
                    "ON CONFLICT(plan_id) DO UPDATE SET plan_id = excluded.plan_id " +
                    "WHERE execution_plans.plan_kind = excluded.plan_kind " +
                    "AND execution_plans.skill_version_id = excluded.skill_version_id " +
                    "AND execution_plans.grant_id = excluded.grant_id " +
                    "AND execution_plans.original_execution_id IS excluded.original_execution_id " +
                    "AND execution_plans.original_plan_id IS excluded.original_plan_id " +
                    "AND execution_plans.original_plan_fingerprint IS excluded.original_plan_fingerprint " +
                    "AND execution_plans.plan_contract_version = excluded.plan_contract_version " +
                    "AND execution_plans.created_utc = excluded.created_utc " +
                    "AND execution_plans.plan_json = excluded.plan_json " +
                    "AND execution_plans.plan_fingerprint = excluded.plan_fingerprint " +
                    "AND execution_plans.expires_utc = excluded.expires_utc;";
                command.Parameters.AddWithValue(
                    "$id",
                    SqliteStorePrimitives.FormatGuid(planId.Value));
                command.Parameters.AddWithValue(
                    "$kind",
                    kind == PersistedPlanKind.Standard ? "standard" : "recovery");
                command.Parameters.AddWithValue(
                    "$skill_version",
                    SqliteStorePrimitives.SkillVersionKey(skillId, skillVersion));
                command.Parameters.AddWithValue(
                    "$grant",
                    SqliteStorePrimitives.FormatGuid(grantId.Value));
                command.Parameters.AddWithValue(
                    "$original_execution",
                    originalExecutionId is null
                        ? DBNull.Value
                        : SqliteStorePrimitives.FormatGuid(originalExecutionId.Value.Value));
                command.Parameters.AddWithValue(
                    "$original_plan",
                    originalPlanId is null
                        ? DBNull.Value
                        : SqliteStorePrimitives.FormatGuid(originalPlanId.Value.Value));
                command.Parameters.AddWithValue(
                    "$original_fingerprint",
                    (object?)originalPlanFingerprint?.Value ?? DBNull.Value);
                command.Parameters.AddWithValue("$contract", contractVersion);
                command.Parameters.AddWithValue(
                    "$created",
                    SqliteStorePrimitives.FormatUtc(createdUtc));
                command.Parameters.AddWithValue("$json", canonicalJson);
                command.Parameters.AddWithValue("$fingerprint", fingerprint.Value);
                command.Parameters.AddWithValue(
                    "$expires",
                    SqliteStorePrimitives.FormatUtc(expiresUtc));
                if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                {
                    throw new PersistenceConflictException("persistence.plan_conflict");
                }
            },
            cancellationToken);
    }

    private static async Task ValidatePlanPrerequisitesAsync(
        SqliteConnection connection,
        SkillId skillId,
        SkillVersionNumber skillVersion,
        GrantId grantId,
        string specificationHash,
        string rootIdentity,
        string capabilitiesJson,
        DateTimeOffset createdUtc,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT v.spec_hash, g.root_identity, g.capabilities_json, g.issued_utc, " +
            "g.expires_utc, g.revoked_utc, s.companion_id, g.companion_id " +
            "FROM skill_versions v JOIN skills s ON s.skill_id = v.skill_id " +
            "JOIN resource_grants g ON g.grant_id = $grant " +
            "WHERE v.skill_version_id = $skill_version AND s.disabled_utc IS NULL " +
            "AND g.resource_type = 'local_folder';";
        command.Parameters.AddWithValue(
            "$grant",
            SqliteStorePrimitives.FormatGuid(grantId.Value));
        command.Parameters.AddWithValue(
            "$skill_version",
            SqliteStorePrimitives.SkillVersionKey(skillId, skillVersion));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            !string.Equals(reader.GetString(0), specificationHash, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(1), rootIdentity, StringComparison.Ordinal) ||
            !string.Equals(
                reader.GetString(2),
                capabilitiesJson,
                StringComparison.Ordinal) ||
            SqliteStorePrimitives.ParseUtc(reader.GetString(3)) > createdUtc ||
            (!reader.IsDBNull(4) &&
             SqliteStorePrimitives.ParseUtc(reader.GetString(4)) <= createdUtc) ||
            !reader.IsDBNull(5) ||
            !string.Equals(reader.GetString(6), reader.GetString(7), StringComparison.Ordinal))
        {
            throw new PersistenceConflictException(
                "persistence.plan_authority_mismatch");
        }
    }

    private static async Task UpsertSkillAsync(
        SqliteConnection connection,
        SkillVersionStateRecord record,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO skills " +
            "(skill_id, companion_id, display_name, created_utc, current_version_id) " +
            "VALUES ($id, $companion, $name, $created, NULL) " +
            "ON CONFLICT(skill_id) DO UPDATE SET display_name = excluded.display_name " +
            "WHERE skills.companion_id = excluded.companion_id " +
            "AND skills.created_utc = excluded.created_utc " +
            "AND skills.disabled_utc IS NULL;";
        command.Parameters.AddWithValue(
            "$id",
            SqliteStorePrimitives.FormatGuid(record.Version.SkillId.Value));
        command.Parameters.AddWithValue(
            "$companion",
            SqliteStorePrimitives.FormatGuid(record.CompanionId.Value));
        command.Parameters.AddWithValue("$name", record.DisplayName);
        command.Parameters.AddWithValue(
            "$created",
            SqliteStorePrimitives.FormatUtc(record.SkillCreatedUtc));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new PersistenceConflictException("persistence.skill_conflict");
        }
    }

    private static async Task<PersistedSkillVersion?> ReadPersistedSkillVersionAsync(
        SqliteConnection connection,
        string versionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT skill_id, version_number, parent_version_id, schema_version, " +
            "skill_spec_json, spec_hash, compiler_id, compiler_version, " +
            "executor_compatibility, lifecycle_state, created_utc, approved_utc, " +
            "semantic_diff_json FROM skill_versions WHERE skill_version_id = $id;";
        command.Parameters.AddWithValue("$id", versionId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new PersistedSkillVersion(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12));
    }

    private static async Task InsertSkillVersionAsync(
        SqliteConnection connection,
        SkillVersionStateRecord record,
        string versionId,
        CancellationToken cancellationToken)
    {
        SkillVersion version = record.Version;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO skill_versions " +
            "(skill_version_id, skill_id, version_number, parent_version_id, schema_version, " +
            "skill_spec_json, spec_hash, compiler_id, compiler_version, " +
            "executor_compatibility, lifecycle_state, created_utc, approved_utc, " +
            "semantic_diff_json) VALUES " +
            "($id, $skill, $number, $parent, $schema, $json, $hash, $compiler_id, " +
            "$compiler_version, $executor, $lifecycle, $created, $approved, $diff);";
        AddSkillVersionParameters(command, record, versionId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new PersistenceConflictException(
                "persistence.skill_version_write_failed");
        }
    }

    private static void ValidateExistingSkillVersion(
        PersistedSkillVersion existing,
        SkillVersionStateRecord record,
        string versionId)
    {
        SkillVersion version = record.Version;
        string? expectedParent = version.Parent is null
            ? null
            : SqliteStorePrimitives.SkillVersionKey(version.SkillId, version.Parent.Value);
        string approved = record.ApprovedUtc is null
            ? string.Empty
            : SqliteStorePrimitives.FormatUtc(record.ApprovedUtc.Value);
        if (!string.Equals(
                existing.SkillId,
                SqliteStorePrimitives.FormatGuid(version.SkillId.Value),
                StringComparison.Ordinal) ||
            existing.VersionNumber != version.Number.Value ||
            !string.Equals(existing.ParentVersionId, expectedParent, StringComparison.Ordinal) ||
            !string.Equals(existing.SchemaVersion, record.SchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(existing.SkillSpecJson, record.SkillSpecJson, StringComparison.Ordinal) ||
            !string.Equals(existing.SpecHash, version.SpecificationHash, StringComparison.Ordinal) ||
            !string.Equals(existing.CompilerId, record.CompilerId, StringComparison.Ordinal) ||
            !string.Equals(existing.CompilerVersion, version.CompilerVersion, StringComparison.Ordinal) ||
            !string.Equals(
                existing.ExecutorCompatibility,
                version.MinimumExecutorVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                existing.CreatedUtc,
                SqliteStorePrimitives.FormatUtc(version.CreatedAt),
                StringComparison.Ordinal) ||
            !SkillTransitionAllowed(
                existing.LifecycleState,
                SqliteStorePrimitives.ToStorage(version.Lifecycle)) ||
            (existing.ApprovedUtc is not null &&
             !string.Equals(existing.ApprovedUtc, approved, StringComparison.Ordinal)) ||
            (existing.SemanticDiffJson is not null &&
             !string.Equals(
                 existing.SemanticDiffJson,
                 record.SemanticDiffJson,
                 StringComparison.Ordinal)) ||
            !string.Equals(
                versionId,
                SqliteStorePrimitives.SkillVersionKey(version.SkillId, version.Number),
                StringComparison.Ordinal))
        {
            throw new PersistenceConflictException(
                "persistence.skill_version_conflict");
        }
    }

    private static async Task UpdateSkillProjectionAsync(
        SqliteConnection connection,
        SkillVersionStateRecord record,
        string versionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "UPDATE skill_versions SET lifecycle_state = $lifecycle, " +
            "approved_utc = COALESCE(approved_utc, $approved), " +
            "semantic_diff_json = COALESCE(semantic_diff_json, $diff) " +
            "WHERE skill_version_id = $id;";
        command.Parameters.AddWithValue("$id", versionId);
        command.Parameters.AddWithValue(
            "$lifecycle",
            SqliteStorePrimitives.ToStorage(record.Version.Lifecycle));
        command.Parameters.AddWithValue(
            "$approved",
            record.ApprovedUtc is null
                ? DBNull.Value
                : SqliteStorePrimitives.FormatUtc(record.ApprovedUtc.Value));
        command.Parameters.AddWithValue(
            "$diff",
            (object?)record.SemanticDiffJson ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new PersistenceConflictException(
                "persistence.skill_version_update_failed");
        }
    }

    private static async Task AdvanceCurrentSkillVersionAsync(
        SqliteConnection connection,
        SkillId skillId,
        SkillVersionNumber version,
        string versionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "UPDATE skills SET current_version_id = $version_id " +
            "WHERE skill_id = $skill AND (current_version_id IS NULL OR " +
            "(SELECT version_number FROM skill_versions " +
            "WHERE skill_version_id = skills.current_version_id) <= $number);";
        command.Parameters.AddWithValue("$version_id", versionId);
        command.Parameters.AddWithValue(
            "$skill",
            SqliteStorePrimitives.FormatGuid(skillId.Value));
        command.Parameters.AddWithValue("$number", version.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new PersistenceConflictException(
                "persistence.skill_current_version_regressed");
        }
    }

    private static void AddSkillVersionParameters(
        SqliteCommand command,
        SkillVersionStateRecord record,
        string versionId)
    {
        SkillVersion version = record.Version;
        command.Parameters.AddWithValue("$id", versionId);
        command.Parameters.AddWithValue(
            "$skill",
            SqliteStorePrimitives.FormatGuid(version.SkillId.Value));
        command.Parameters.AddWithValue("$number", version.Number.Value);
        command.Parameters.AddWithValue(
            "$parent",
            version.Parent is null
                ? DBNull.Value
                : SqliteStorePrimitives.SkillVersionKey(
                    version.SkillId,
                    version.Parent.Value));
        command.Parameters.AddWithValue("$schema", record.SchemaVersion);
        command.Parameters.AddWithValue("$json", record.SkillSpecJson);
        command.Parameters.AddWithValue("$hash", version.SpecificationHash);
        command.Parameters.AddWithValue("$compiler_id", record.CompilerId);
        command.Parameters.AddWithValue("$compiler_version", version.CompilerVersion);
        command.Parameters.AddWithValue("$executor", version.MinimumExecutorVersion);
        command.Parameters.AddWithValue(
            "$lifecycle",
            SqliteStorePrimitives.ToStorage(version.Lifecycle));
        command.Parameters.AddWithValue(
            "$created",
            SqliteStorePrimitives.FormatUtc(version.CreatedAt));
        command.Parameters.AddWithValue(
            "$approved",
            record.ApprovedUtc is null
                ? DBNull.Value
                : SqliteStorePrimitives.FormatUtc(record.ApprovedUtc.Value));
        command.Parameters.AddWithValue(
            "$diff",
            (object?)record.SemanticDiffJson ?? DBNull.Value);
    }

    private static bool SkillTransitionAllowed(string current, string next) =>
        current == next ||
        next == "stale" ||
        (current, next) is
            ("draft", "approved") or
            ("approved", "practiced") or
            ("practiced", "reliable") or
            ("reliable", "delegated");

    private static bool IsCanonicalPlanDocument(
        string? json,
        string expectedContract,
        string expectedFingerprint,
        PlanId expectedPlanId,
        SkillId expectedSkillId,
        SkillVersionNumber expectedSkillVersion,
        GrantId expectedGrantId,
        DateTimeOffset expectedCreatedUtc,
        DateTimeOffset expectedExpiresUtc,
        ExecutionId? originalExecutionId,
        PlanId? originalPlanId,
        PlanFingerprint? originalPlanFingerprint)
    {
        if (!SqliteStorePrimitives.IsValidJson(json) ||
            !SqliteStorePrimitives.HashMatches(json!, expectedFingerprint))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json!);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !HasExactString(root, "contractVersion", expectedContract) ||
                !HasExactString(
                    root,
                    "planId",
                    SqliteStorePrimitives.FormatGuid(expectedPlanId.Value)) ||
                !HasExactString(
                    root,
                    "createdUtc",
                    SqliteStorePrimitives.FormatUtc(expectedCreatedUtc)) ||
                !HasExactString(
                    root,
                    "expiresUtc",
                    SqliteStorePrimitives.FormatUtc(expectedExpiresUtc)) ||
                !root.TryGetProperty("skill", out JsonElement skill) ||
                skill.ValueKind != JsonValueKind.Object ||
                !HasExactString(
                    skill,
                    "id",
                    SqliteStorePrimitives.FormatGuid(expectedSkillId.Value)) ||
                !skill.TryGetProperty("version", out JsonElement version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out int persistedVersion) ||
                persistedVersion != expectedSkillVersion.Value ||
                !root.TryGetProperty("grant", out JsonElement grant) ||
                grant.ValueKind != JsonValueKind.Object ||
                !HasExactString(
                    grant,
                    "id",
                    SqliteStorePrimitives.FormatGuid(expectedGrantId.Value)))
            {
                return false;
            }

            bool isRecovery = string.Equals(
                expectedContract,
                "tooltail.recovery-plan/1",
                StringComparison.Ordinal);
            if (!isRecovery)
            {
                return !root.TryGetProperty("originalExecution", out _);
            }

            if (!root.TryGetProperty(
                    "originalExecution",
                    out JsonElement original) ||
                original.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return originalExecutionId is not null &&
                originalPlanId is not null &&
                originalPlanFingerprint is not null &&
                HasExactString(
                    original,
                    "executionId",
                    SqliteStorePrimitives.FormatGuid(originalExecutionId.Value.Value)) &&
                HasExactString(
                    original,
                    "planId",
                    SqliteStorePrimitives.FormatGuid(originalPlanId.Value.Value)) &&
                HasExactString(
                    original,
                    "planFingerprint",
                    originalPlanFingerprint.Value);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasExactString(
        JsonElement parent,
        string propertyName,
        string expected) =>
        parent.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String &&
        string.Equals(value.GetString(), expected, StringComparison.Ordinal);

    private static bool PlanDocumentMatchesStoredAuthority(
        string planJson,
        string specificationHash,
        string rootIdentity,
        string capabilitiesJson)
    {
        if (!SqliteStorePrimitives.IsValidJson(planJson) ||
            !SqliteStorePrimitives.IsValidJson(capabilitiesJson))
        {
            return false;
        }

        try
        {
            using JsonDocument plan = JsonDocument.Parse(planJson);
            using JsonDocument capabilities = JsonDocument.Parse(capabilitiesJson);
            if (!plan.RootElement.TryGetProperty("skill", out JsonElement skill) ||
                !HasExactString(
                    skill,
                    "specificationSha256",
                    specificationHash) ||
                !plan.RootElement.TryGetProperty("grant", out JsonElement grant) ||
                !HasExactString(grant, "rootIdentity", rootIdentity) ||
                !grant.TryGetProperty("actions", out JsonElement actions) ||
                actions.ValueKind != JsonValueKind.Array ||
                capabilities.RootElement.ValueKind != JsonValueKind.Array ||
                actions.GetArrayLength() != capabilities.RootElement.GetArrayLength())
            {
                return false;
            }

            using JsonElement.ArrayEnumerator actionValues = actions.EnumerateArray();
            using JsonElement.ArrayEnumerator capabilityValues =
                capabilities.RootElement.EnumerateArray();
            while (actionValues.MoveNext() && capabilityValues.MoveNext())
            {
                if (actionValues.Current.ValueKind != JsonValueKind.String ||
                    capabilityValues.Current.ValueKind != JsonValueKind.String ||
                    !string.Equals(
                        actionValues.Current.GetString(),
                        capabilityValues.Current.GetString(),
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool PlanDocumentMatchesDefinition(
        string json,
        ExecutionPlanDefinition definition)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (!HasExactProperties(
                    root,
                    "contractVersion",
                    "planId",
                    "skill",
                    "grant",
                    "createdUtc",
                    "expiresUtc",
                    "operations") ||
                !MatchesSkill(root, definition) ||
                !MatchesGrant(
                    root,
                    definition.GrantId,
                    definition.RootIdentity.Value,
                    definition.GrantedCapabilities.Select(
                        SqliteStorePrimitives.ToStorage)) ||
                !root.TryGetProperty("operations", out JsonElement operations) ||
                operations.ValueKind != JsonValueKind.Array ||
                operations.GetArrayLength() != definition.Operations.Count)
            {
                return false;
            }

            int index = 0;
            foreach (JsonElement persisted in operations.EnumerateArray())
            {
                PlannedFileOperation expected = definition.Operations[index++];
                if (!HasExactProperties(
                        persisted,
                        "sequence",
                        "primitive",
                        "sourceRelativePath",
                        "destinationRelativePath",
                        "destinationPrecondition",
                        "sourceFingerprint",
                        "expectedSourceState",
                        "expectedDestinationState") ||
                    !HasExactInt(persisted, "sequence", expected.Sequence) ||
                    !HasExactString(
                        persisted,
                        "primitive",
                        SqliteStorePrimitives.ToStorage(expected.Primitive)) ||
                    !HasOptionalString(
                        persisted,
                        "sourceRelativePath",
                        expected.SourceRelativePath) ||
                    !HasExactString(
                        persisted,
                        "destinationRelativePath",
                        expected.DestinationRelativePath) ||
                    !HasExactString(
                        persisted,
                        "destinationPrecondition",
                        ToStorage(expected.DestinationPrecondition)) ||
                    !MatchesSourceFingerprint(persisted, expected.SourceFingerprint) ||
                    !HasExactString(
                        persisted,
                        "expectedSourceState",
                        ToStorage(expected.ExpectedSourceState)) ||
                    !HasExactString(
                        persisted,
                        "expectedDestinationState",
                        ToStorage(expected.ExpectedDestinationState)))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or JsonException or InvalidOperationException or
            OverflowException)
        {
            return false;
        }
    }

    private static bool PlanDocumentMatchesDefinition(
        string json,
        RecoveryPlanDefinition definition)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (!HasExactProperties(
                    root,
                    "contractVersion",
                    "planId",
                    "originalExecution",
                    "skill",
                    "grant",
                    "createdUtc",
                    "expiresUtc",
                    "operations") ||
                !MatchesSkill(root, definition) ||
                !MatchesGrant(
                    root,
                    definition.GrantId,
                    definition.RootIdentity.Value,
                    definition.GrantedCapabilities.Select(
                        SqliteStorePrimitives.ToStorage)) ||
                !root.TryGetProperty("operations", out JsonElement operations) ||
                operations.ValueKind != JsonValueKind.Array ||
                operations.GetArrayLength() != definition.Operations.Count)
            {
                return false;
            }

            int index = 0;
            foreach (JsonElement persisted in operations.EnumerateArray())
            {
                PlannedRecoveryOperation expected = definition.Operations[index++];
                if (!HasExactProperties(
                        persisted,
                        "sequence",
                        "originalStepSequence",
                        "originalPrimitive",
                        "recoveryPrimitive",
                        "sourceRelativePath",
                        "destinationRelativePath",
                        "originalDestinationWasAbsent",
                        "expectedSource") ||
                    !HasExactInt(persisted, "sequence", expected.Sequence) ||
                    !HasExactInt(
                        persisted,
                        "originalStepSequence",
                        expected.OriginalStepSequence) ||
                    !HasExactString(
                        persisted,
                        "originalPrimitive",
                        SqliteStorePrimitives.ToStorage(expected.OriginalPrimitive)) ||
                    !HasExactString(
                        persisted,
                        "recoveryPrimitive",
                        SqliteStorePrimitives.ToStorage(expected.Primitive)) ||
                    !HasExactString(
                        persisted,
                        "sourceRelativePath",
                        expected.SourceRelativePath) ||
                    !HasOptionalString(
                        persisted,
                        "destinationRelativePath",
                        expected.DestinationRelativePath) ||
                    !HasExactBoolean(
                        persisted,
                        "originalDestinationWasAbsent",
                        expected.OriginalDestinationWasAbsent) ||
                    !persisted.TryGetProperty(
                        "expectedSource",
                        out JsonElement expectedSource) ||
                    !MatchesEntryEvidence(expectedSource, expected.ExpectedSource))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or JsonException or InvalidOperationException or
            OverflowException)
        {
            return false;
        }
    }

    private static bool MatchesSkill(
        JsonElement root,
        ExecutionPlanDefinition definition) =>
        root.TryGetProperty("skill", out JsonElement skill) &&
        HasExactProperties(skill, "id", "version", "specificationSha256") &&
        HasExactString(
            skill,
            "id",
            SqliteStorePrimitives.FormatGuid(definition.SkillId.Value)) &&
        HasExactInt(skill, "version", definition.SkillVersion.Value) &&
        HasExactString(
            skill,
            "specificationSha256",
            definition.SkillSpecificationHash.Value);

    private static bool MatchesSkill(
        JsonElement root,
        RecoveryPlanDefinition definition) =>
        root.TryGetProperty("skill", out JsonElement skill) &&
        HasExactProperties(skill, "id", "version", "specificationSha256") &&
        HasExactString(
            skill,
            "id",
            SqliteStorePrimitives.FormatGuid(definition.SkillId.Value)) &&
        HasExactInt(skill, "version", definition.SkillVersion.Value) &&
        HasExactString(
            skill,
            "specificationSha256",
            definition.SkillSpecificationHash.Value);

    private static bool MatchesGrant(
        JsonElement root,
        GrantId grantId,
        string rootIdentity,
        IEnumerable<string> capabilities)
    {
        if (!root.TryGetProperty("grant", out JsonElement grant) ||
            !HasExactProperties(grant, "id", "rootIdentity", "actions") ||
            !HasExactString(
                grant,
                "id",
                SqliteStorePrimitives.FormatGuid(grantId.Value)) ||
            !HasExactString(grant, "rootIdentity", rootIdentity) ||
            !grant.TryGetProperty("actions", out JsonElement actions) ||
            actions.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        string[] expected = capabilities.Order(StringComparer.Ordinal).ToArray();
        if (actions.GetArrayLength() != expected.Length)
        {
            return false;
        }

        int index = 0;
        foreach (JsonElement action in actions.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.String ||
                !string.Equals(
                    action.GetString(),
                    expected[index++],
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesSourceFingerprint(
        JsonElement operation,
        SourceFileFingerprint? expected)
    {
        if (!operation.TryGetProperty(
                "sourceFingerprint",
                out JsonElement fingerprint))
        {
            return false;
        }

        if (expected is null)
        {
            return fingerprint.ValueKind == JsonValueKind.Null;
        }

        return HasExactProperties(
                fingerprint,
                "entryIdentity",
                "length",
                "lastWriteUtc",
                "contentSha256") &&
            HasExactString(
                fingerprint,
                "entryIdentity",
                expected.EntryIdentity) &&
            HasExactInt64(fingerprint, "length", expected.Length) &&
            HasExactString(
                fingerprint,
                "lastWriteUtc",
                SqliteStorePrimitives.FormatUtc(expected.LastWriteUtc)) &&
            HasOptionalString(
                fingerprint,
                "contentSha256",
                expected.ContentHash?.Value);
    }

    private static bool MatchesEntryEvidence(
        JsonElement persisted,
        VerifiedEntryEvidence expected) =>
        HasExactProperties(
            persisted,
            "kind",
            "volumeIdentity",
            "entryIdentity",
            "length",
            "creationUtc",
            "lastWriteUtc",
            "attributes",
            "contentSha256") &&
        HasExactString(
            persisted,
            "kind",
            expected.Kind == VerifiedEntryKind.File ? "file" : "directory") &&
        HasExactString(persisted, "volumeIdentity", expected.VolumeIdentity) &&
        HasExactString(persisted, "entryIdentity", expected.EntryIdentity) &&
        HasOptionalInt64(persisted, "length", expected.Length) &&
        HasExactString(
            persisted,
            "creationUtc",
            SqliteStorePrimitives.FormatUtc(expected.CreationUtc)) &&
        HasExactString(
            persisted,
            "lastWriteUtc",
            SqliteStorePrimitives.FormatUtc(expected.LastWriteUtc)) &&
        HasExactInt(persisted, "attributes", expected.Attributes) &&
        HasOptionalString(
            persisted,
            "contentSha256",
            expected.ContentHash?.Value);

    private static bool HasExactProperties(
        JsonElement element,
        params string[] propertyNames) =>
        element.ValueKind == JsonValueKind.Object &&
        element.EnumerateObject().Count() == propertyNames.Length &&
        propertyNames.All(name => element.TryGetProperty(name, out _));

    private static bool HasExactInt(
        JsonElement parent,
        string propertyName,
        int expected) =>
        parent.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int parsed) &&
        parsed == expected;

    private static bool HasExactInt64(
        JsonElement parent,
        string propertyName,
        long expected) =>
        parent.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out long parsed) &&
        parsed == expected;

    private static bool HasOptionalInt64(
        JsonElement parent,
        string propertyName,
        long? expected)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value))
        {
            return false;
        }

        return expected is null
            ? value.ValueKind == JsonValueKind.Null
            : value.ValueKind == JsonValueKind.Number &&
              value.TryGetInt64(out long parsed) &&
              parsed == expected.Value;
    }

    private static bool HasExactBoolean(
        JsonElement parent,
        string propertyName,
        bool expected) =>
        parent.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean() == expected;

    private static bool HasOptionalString(
        JsonElement parent,
        string propertyName,
        string? expected)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value))
        {
            return false;
        }

        return expected is null
            ? value.ValueKind == JsonValueKind.Null
            : value.ValueKind == JsonValueKind.String &&
              string.Equals(value.GetString(), expected, StringComparison.Ordinal);
    }

    private static string ToStorage(DestinationPrecondition value) =>
        value switch
        {
            DestinationPrecondition.Absent => "absent",
            DestinationPrecondition.ExistingDirectory => "existing_directory",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static string ToStorage(ExpectedSourceState value) =>
        value switch
        {
            ExpectedSourceState.NotApplicable => "not_applicable",
            ExpectedSourceState.Absent => "absent",
            ExpectedSourceState.Unchanged => "unchanged",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static string ToStorage(ExpectedDestinationState value) =>
        value switch
        {
            ExpectedDestinationState.DirectoryPresent => "directory_present",
            ExpectedDestinationState.FileMatchesSource => "file_matches_source",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private sealed record PersistedSkillVersion(
        string SkillId,
        int VersionNumber,
        string? ParentVersionId,
        string SchemaVersion,
        string SkillSpecJson,
        string SpecHash,
        string CompilerId,
        string CompilerVersion,
        string ExecutorCompatibility,
        string LifecycleState,
        string CreatedUtc,
        string? ApprovedUtc,
        string? SemanticDiffJson);
}
