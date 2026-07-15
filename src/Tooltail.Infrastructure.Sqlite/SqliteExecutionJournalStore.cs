using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tooltail.Application.Abstractions;
using Tooltail.Domain.Common;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Infrastructure.Sqlite;

public sealed class SqliteExecutionJournalStore :
    IExecutionJournalStore,
    IExecutionJournalReader
{
    private const int MaximumRecoveryCandidates = 1_000;

    private readonly TooltailSqliteDatabase database;

    public SqliteExecutionJournalStore(TooltailSqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        this.database = database;
    }

    public ValueTask<JournalWriteResult> CreateAsync(
        ExecutionJournal journal,
        PlanApproval consumedApproval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(consumedApproval);
        if (!IsValidNewJournal(journal, consumedApproval))
        {
            return ValueTask.FromResult(
                JournalWriteResult.Failure("persistence.journal_open_invalid"));
        }

        return WriteAsync(
            async (connection, token) =>
            {
                ExistingExecutionMatch existing = await MatchExistingExecutionAsync(
                    connection,
                    journal,
                    consumedApproval,
                    token).ConfigureAwait(false);
                if (existing == ExistingExecutionMatch.Exact)
                {
                    return;
                }

                if (existing == ExistingExecutionMatch.Conflict)
                {
                    throw new PersistenceConflictException(
                        "persistence.execution_conflict");
                }

                PersistedPlanContext plan = await LoadPlanContextAsync(
                    connection,
                    journal.PlanId,
                    token).ConfigureAwait(false) ??
                    throw new PersistenceConflictException(
                        "persistence.execution_plan_missing");
                if (!PlanMatchesJournal(plan, journal) ||
                    !string.Equals(plan.Status, "approved", StringComparison.Ordinal))
                {
                    throw new PersistenceConflictException(
                        "persistence.execution_plan_invalid");
                }

                int consumed = await ConsumeApprovalAsync(
                    connection,
                    consumedApproval,
                    token).ConfigureAwait(false);
                if (consumed != 1)
                {
                    throw new PersistenceConflictException(
                        "persistence.approval_already_used");
                }

                await InsertExecutionAsync(
                    connection,
                    journal,
                    consumedApproval.Id,
                    token).ConfigureAwait(false);
                await SqliteJournalCodec.InsertEventAsync(
                    connection,
                    journal.Events[0],
                    token).ConfigureAwait(false);
                await SetPlanConsumedAsync(
                    connection,
                    journal.PlanId,
                    journal.PlanFingerprint,
                    token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public ValueTask<JournalWriteResult> AppendAsync(
        ExecutionJournalEvent journalEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journalEvent);
        return WriteAsync(
            async (connection, token) =>
            {
                DomainResult<ExecutionJournal> loaded =
                    await LoadValidatedJournalAsync(
                        connection,
                        journalEvent.ExecutionId,
                        token).ConfigureAwait(false);
                if (!loaded.IsSuccess)
                {
                    throw new PersistenceConflictException(
                        loaded.Error?.Code ?? "persistence.journal_not_found");
                }

                ExecutionJournal journal = loaded.Value!;
                if (journalEvent.EventSequence <= journal.Events.Count)
                {
                    ExecutionJournalEvent persisted =
                        journal.Events[checked((int)journalEvent.EventSequence - 1)];
                    if (SqliteJournalCodec.Equivalent(persisted, journalEvent))
                    {
                        return;
                    }

                    throw new PersistenceConflictException(
                        "persistence.journal_event_conflict");
                }

                DomainResult<ExecutionJournal> appended = journal.Append(journalEvent);
                if (!appended.IsSuccess)
                {
                    throw new PersistenceConflictException(
                        "persistence.journal_transition_rejected");
                }

                await SqliteJournalCodec.InsertEventAsync(
                    connection,
                    journalEvent,
                    token).ConfigureAwait(false);
                if (journalEvent is not StepRolledBackEvent)
                {
                    await UpdateExecutionStatusAsync(
                        connection,
                        appended.Value!,
                        token).ConfigureAwait(false);
                }
            },
            cancellationToken);
    }

    public ValueTask<JournalWriteResult> StoreReceiptAsync(
        ExecutionReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!SqliteReceiptCodec.IsWithinBounds(receipt))
        {
            return ValueTask.FromResult(
                JournalWriteResult.Failure("persistence.receipt_invalid"));
        }

        return WriteAsync(
            async (connection, token) =>
            {
                DomainResult<ExecutionJournal> loaded =
                    await LoadValidatedJournalAsync(
                        connection,
                        receipt.ExecutionId,
                        token).ConfigureAwait(false);
                if (!loaded.IsSuccess)
                {
                    throw new PersistenceConflictException(
                        "persistence.receipt_journal_invalid");
                }

                ExecutionJournal journal = loaded.Value!;
                PersistedPlanContext plan = await LoadPlanContextAsync(
                    connection,
                    journal.PlanId,
                    token).ConfigureAwait(false) ??
                    throw new PersistenceConflictException(
                        "persistence.receipt_plan_missing");
                if (!PlanMatchesJournal(plan, journal))
                {
                    throw new PersistenceConflictException(
                        "persistence.receipt_plan_invalid");
                }

                string encoded = SqliteReceiptCodec.Encode(receipt);
                DomainResult<ExecutionReceipt> validated =
                    SqliteReceiptCodec.DecodeStandard(
                        encoded,
                        receipt.Id,
                        journal,
                        receipt.CompletedUtc,
                        receipt.UndoAvailableUntilUtc,
                        plan.CanonicalJson);
                if (!validated.IsSuccess ||
                    receipt.ExecutionId != journal.ExecutionId ||
                    receipt.PlanId != journal.PlanId ||
                    receipt.PlanFingerprint != journal.PlanFingerprint)
                {
                    throw new PersistenceConflictException(
                        validated.Error?.Code ?? "persistence.receipt_invalid");
                }

                await InsertReceiptAsync(
                    connection,
                    receipt.Id,
                    receipt.ExecutionId,
                    "standard",
                    encoded,
                    receipt.CompletedUtc,
                    receipt.UndoAvailableUntilUtc,
                    token).ConfigureAwait(false);
                await CompleteExecutionAsync(
                    connection,
                    receipt.ExecutionId,
                    receipt.CompletedUtc,
                    receipt.VerifiedStepCount,
                    token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public ValueTask<JournalWriteResult> StoreRecoveryReceiptAsync(
        RecoveryExecutionReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!SqliteReceiptCodec.IsWithinBounds(receipt))
        {
            return ValueTask.FromResult(
                JournalWriteResult.Failure("persistence.receipt_invalid"));
        }

        return WriteAsync(
            async (connection, token) =>
            {
                DomainResult<ExecutionJournal> recoveryLoaded =
                    await LoadValidatedJournalAsync(
                        connection,
                        receipt.ExecutionId,
                        token).ConfigureAwait(false);
                DomainResult<ExecutionJournal> originalLoaded =
                    await LoadValidatedJournalAsync(
                        connection,
                        receipt.OriginalExecutionId,
                        token).ConfigureAwait(false);
                if (!recoveryLoaded.IsSuccess || !originalLoaded.IsSuccess)
                {
                    throw new PersistenceConflictException(
                        "persistence.receipt_journal_invalid");
                }

                ExecutionJournal recoveryJournal = recoveryLoaded.Value!;
                ExecutionJournal originalJournal = originalLoaded.Value!;
                PersistedPlanContext plan = await LoadPlanContextAsync(
                    connection,
                    recoveryJournal.PlanId,
                    token).ConfigureAwait(false) ??
                    throw new PersistenceConflictException(
                        "persistence.receipt_plan_missing");
                if (!PlanMatchesJournal(plan, recoveryJournal) ||
                    plan.OriginalExecutionId != originalJournal.ExecutionId ||
                    plan.OriginalPlanId != originalJournal.PlanId ||
                    plan.OriginalPlanFingerprint != originalJournal.PlanFingerprint)
                {
                    throw new PersistenceConflictException(
                        "persistence.receipt_plan_invalid");
                }

                string encoded = SqliteReceiptCodec.Encode(receipt);
                DomainResult<RecoveryExecutionReceipt> validated =
                    SqliteReceiptCodec.DecodeRecovery(
                        encoded,
                        receipt.Id,
                        recoveryJournal,
                        originalJournal,
                        receipt.CompletedUtc,
                        plan.CanonicalJson);
                if (!validated.IsSuccess ||
                    receipt.ExecutionId != recoveryJournal.ExecutionId ||
                    receipt.PlanId != recoveryJournal.PlanId ||
                    receipt.PlanFingerprint != recoveryJournal.PlanFingerprint ||
                    receipt.OriginalExecutionId != originalJournal.ExecutionId ||
                    receipt.OriginalPlanId != originalJournal.PlanId ||
                    receipt.OriginalPlanFingerprint != originalJournal.PlanFingerprint)
                {
                    throw new PersistenceConflictException(
                        validated.Error?.Code ?? "persistence.receipt_invalid");
                }

                await InsertReceiptAsync(
                    connection,
                    receipt.Id,
                    receipt.ExecutionId,
                    "recovery",
                    encoded,
                    receipt.CompletedUtc,
                    undoAvailableUntilUtc: null,
                    token).ConfigureAwait(false);
                await CompleteExecutionAsync(
                    connection,
                    receipt.ExecutionId,
                    receipt.CompletedUtc,
                    receipt.VerifiedSteps.Count,
                    token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public async ValueTask<ExecutionJournalReadResult> LoadJournalAsync(
        ExecutionId executionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = await database.OpenReadOnlyAsync(
                cancellationToken).ConfigureAwait(false);
            DomainResult<ExecutionJournal> loaded =
                await LoadValidatedJournalAsync(
                    connection,
                    executionId,
                    cancellationToken).ConfigureAwait(false);
            return loaded.IsSuccess
                ? ExecutionJournalReadResult.Success(loaded.Value!)
                : ExecutionJournalReadResult.Failure(
                    loaded.Error?.Code ?? "persistence.journal_corrupt");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedStoreFailure(exception))
        {
            return ExecutionJournalReadResult.Failure(
                SqliteStorePrimitives.MapFailure(exception));
        }
    }

    public async ValueTask<ExecutionReceiptReadResult> LoadReceiptAsync(
        ExecutionId executionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = await database.OpenReadOnlyAsync(
                cancellationToken).ConfigureAwait(false);
            PersistedReceipt? persisted = await ReadReceiptAsync(
                connection,
                executionId,
                cancellationToken).ConfigureAwait(false);
            if (persisted is null)
            {
                return ExecutionReceiptReadResult.Failure(
                    "persistence.receipt_not_found");
            }

            DomainResult<ExecutionJournal> journalLoaded =
                await LoadValidatedJournalAsync(
                    connection,
                    executionId,
                    cancellationToken).ConfigureAwait(false);
            if (!journalLoaded.IsSuccess)
            {
                return ExecutionReceiptReadResult.Failure(
                    "persistence.receipt_journal_invalid");
            }

            ExecutionJournal journal = journalLoaded.Value!;
            PersistedPlanContext plan = await LoadPlanContextAsync(
                connection,
                journal.PlanId,
                cancellationToken).ConfigureAwait(false) ??
                throw new FormatException("Receipt plan is missing.");
            if (!PlanMatchesJournal(plan, journal))
            {
                return ExecutionReceiptReadResult.Failure(
                    "persistence.receipt_plan_invalid");
            }

            if (string.Equals(persisted.Kind, "standard", StringComparison.Ordinal))
            {
                DomainResult<ExecutionReceipt> decoded =
                    SqliteReceiptCodec.DecodeStandard(
                        persisted.Json,
                        persisted.Id,
                        journal,
                        persisted.CreatedUtc,
                        persisted.UndoAvailableUntilUtc,
                        plan.CanonicalJson);
                return decoded.IsSuccess
                    ? ExecutionReceiptReadResult.Standard(decoded.Value!)
                    : ExecutionReceiptReadResult.Failure(
                        "persistence.receipt_corrupt");
            }

            if (!string.Equals(persisted.Kind, "recovery", StringComparison.Ordinal) ||
                persisted.UndoAvailableUntilUtc is not null ||
                plan.OriginalExecutionId is null)
            {
                return ExecutionReceiptReadResult.Failure(
                    "persistence.receipt_corrupt");
            }

            DomainResult<ExecutionJournal> originalLoaded =
                await LoadValidatedJournalAsync(
                    connection,
                    plan.OriginalExecutionId.Value,
                    cancellationToken).ConfigureAwait(false);
            if (!originalLoaded.IsSuccess)
            {
                return ExecutionReceiptReadResult.Failure(
                    "persistence.receipt_journal_invalid");
            }

            DomainResult<RecoveryExecutionReceipt> recoveryDecoded =
                SqliteReceiptCodec.DecodeRecovery(
                    persisted.Json,
                    persisted.Id,
                    journal,
                    originalLoaded.Value!,
                    persisted.CreatedUtc,
                    plan.CanonicalJson);
            return recoveryDecoded.IsSuccess
                ? ExecutionReceiptReadResult.Recovery(recoveryDecoded.Value!)
                : ExecutionReceiptReadResult.Failure(
                    "persistence.receipt_corrupt");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedStoreFailure(exception))
        {
            return ExecutionReceiptReadResult.Failure(
                SqliteStorePrimitives.MapFailure(exception));
        }
    }

    public async ValueTask<ExecutionRecoveryScanResult> ScanRecoveryRequiredAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = await database.OpenReadOnlyAsync(
                cancellationToken).ConfigureAwait(false);
            ExecutionId[] executionIds = await ReadUnreceiptedExecutionIdsAsync(
                connection,
                cancellationToken).ConfigureAwait(false);
            if (executionIds.Length > MaximumRecoveryCandidates)
            {
                return ExecutionRecoveryScanResult.Failure(
                    "persistence.recovery_scan_bound_exceeded");
            }

            List<ExecutionRecoveryCandidate> candidates = [];
            foreach (ExecutionId candidateId in executionIds)
            {
                DomainResult<ExecutionJournal> loaded =
                    await LoadValidatedJournalAsync(
                        connection,
                        candidateId,
                        cancellationToken).ConfigureAwait(false);
                if (!loaded.IsSuccess)
                {
                    return ExecutionRecoveryScanResult.Failure(
                        "persistence.recovery_scan_corrupt");
                }

                ExecutionJournal journal = loaded.Value!;
                StepRecoveryAssessment[] assessments = Enumerable
                    .Range(1, journal.OperationCount)
                    .Select(journal.AssessStep)
                    .ToArray();
                string reasonCode = assessments.Any(
                    static step => step.RequiresFileSystemInspection)
                        ? "persistence.recovery_inspection_required"
                        : assessments.All(
                            static step => step.Status == StepRecoveryStatus.Verified)
                            ? "persistence.receipt_missing"
                            : "persistence.execution_incomplete";
                candidates.Add(
                    new ExecutionRecoveryCandidate(
                        journal.ExecutionId,
                        journal.Kind,
                        assessments,
                        reasonCode));
            }

            return ExecutionRecoveryScanResult.Success(candidates);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedStoreFailure(exception))
        {
            return ExecutionRecoveryScanResult.Failure(
                SqliteStorePrimitives.MapFailure(exception));
        }
    }

    private async ValueTask<JournalWriteResult> WriteAsync(
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
            return JournalWriteResult.Success;
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

            return JournalWriteResult.Failure(
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

    private static bool IsValidNewJournal(
        ExecutionJournal journal,
        PlanApproval approval)
    {
        if (journal.Events.Count != 1 ||
            journal.Events[0] is not ExecutionOpenedEvent opened ||
            approval.State != PlanApprovalState.Consumed ||
            approval.ConsumedUtc is null ||
            approval.PlanId != journal.PlanId ||
            approval.Fingerprint != journal.PlanFingerprint ||
            approval.ConsumedUtc > opened.OccurredUtc ||
            approval.ConsumedUtc < approval.ApprovedUtc ||
            approval.ConsumedUtc >= approval.ExpiresUtc)
        {
            return false;
        }

        if (journal.OperationCount is < 1 or > 10_000)
        {
            return false;
        }

        return journal.Kind switch
        {
            ExecutionJournalKind.Standard => approval.Purpose is
                PlanApprovalPurpose.Production or PlanApprovalPurpose.Rehearsal,
            ExecutionJournalKind.Recovery =>
                approval.Purpose == PlanApprovalPurpose.Undo,
            _ => false,
        };
    }

    private static async Task<int> ConsumeApprovalAsync(
        SqliteConnection connection,
        PlanApproval approval,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "UPDATE approvals SET consumed_utc = $consumed " +
            "WHERE approval_id = $id AND plan_id = $plan " +
            "AND plan_fingerprint = $fingerprint AND approved_utc = $approved " +
            "AND expires_utc = $expires AND approval_purpose = $purpose " +
            "AND consumed_utc IS NULL AND revoked_utc IS NULL " +
            "AND revocation_reason IS NULL;";
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
            SqliteStorePrimitives.FormatUtc(approval.ConsumedUtc!.Value));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertExecutionAsync(
        SqliteConnection connection,
        ExecutionJournal journal,
        ApprovalId approvalId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO executions " +
            "(execution_id, plan_id, approval_id, correlation_id, journal_kind, " +
            "operation_primitives_json, operation_inverse_kinds_json, " +
            "recovery_primitives_json, recovery_original_steps_json, " +
            "started_utc, completed_utc, status, verification_summary_json, " +
            "residual_effects_json) VALUES " +
            "($execution, $plan, $approval, NULL, $kind, $primitives, $inverses, " +
            "$recovery_primitives, $original_steps, $started, NULL, 'running', " +
            "NULL, NULL);";
        command.Parameters.AddWithValue(
            "$execution",
            SqliteStorePrimitives.FormatGuid(journal.ExecutionId.Value));
        command.Parameters.AddWithValue(
            "$plan",
            SqliteStorePrimitives.FormatGuid(journal.PlanId.Value));
        command.Parameters.AddWithValue(
            "$approval",
            SqliteStorePrimitives.FormatGuid(approvalId.Value));
        command.Parameters.AddWithValue(
            "$kind",
            SqliteStorePrimitives.ToStorage(journal.Kind));
        command.Parameters.AddWithValue(
            "$primitives",
            SqliteJournalCodec.FilePrimitivesJson(journal.OperationPrimitives));
        command.Parameters.AddWithValue(
            "$inverses",
            SqliteJournalCodec.InverseKindsJson(journal.OperationInverseKinds));
        command.Parameters.AddWithValue(
            "$recovery_primitives",
            SqliteJournalCodec.RecoveryPrimitivesJson(
                journal.RecoveryOperationPrimitives));
        command.Parameters.AddWithValue(
            "$original_steps",
            SqliteJournalCodec.OriginalStepsJson(
                journal.RecoveryOriginalStepSequences));
        command.Parameters.AddWithValue(
            "$started",
            SqliteStorePrimitives.FormatUtc(journal.Events[0].OccurredUtc));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new PersistenceConflictException(
                "persistence.execution_insert_failed");
        }
    }

    private static async Task SetPlanConsumedAsync(
        SqliteConnection connection,
        PlanId planId,
        PlanFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "UPDATE execution_plans SET status = 'consumed' " +
            "WHERE plan_id = $plan AND plan_fingerprint = $fingerprint " +
            "AND status = 'approved';";
        command.Parameters.AddWithValue(
            "$plan",
            SqliteStorePrimitives.FormatGuid(planId.Value));
        command.Parameters.AddWithValue("$fingerprint", fingerprint.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new PersistenceConflictException(
                "persistence.execution_plan_conflict");
        }
    }

    private static async Task UpdateExecutionStatusAsync(
        SqliteConnection connection,
        ExecutionJournal journal,
        CancellationToken cancellationToken)
    {
        StepRecoveryAssessment[] steps = Enumerable.Range(1, journal.OperationCount)
            .Select(journal.AssessStep)
            .ToArray();
        string status = steps.Any(
            static step => step.Status == StepRecoveryStatus.RecoveryRequired)
                ? "recovery_required"
                : steps.Any(static step => step.FailureCode is not null)
                    ? "failed"
                    : "running";
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "UPDATE executions SET status = $status " +
            "WHERE execution_id = $execution AND completed_utc IS NULL;";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue(
            "$execution",
            SqliteStorePrimitives.FormatGuid(journal.ExecutionId.Value));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new PersistenceConflictException(
                "persistence.execution_status_conflict");
        }
    }

    private static async Task InsertReceiptAsync(
        SqliteConnection connection,
        ReceiptId receiptId,
        ExecutionId executionId,
        string kind,
        string json,
        DateTimeOffset createdUtc,
        DateTimeOffset? undoAvailableUntilUtc,
        CancellationToken cancellationToken)
    {
        PersistedReceipt? existing = await ReadReceiptAsync(
            connection,
            executionId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Id == receiptId &&
                string.Equals(existing.Kind, kind, StringComparison.Ordinal) &&
                string.Equals(existing.Json, json, StringComparison.Ordinal) &&
                existing.CreatedUtc == createdUtc &&
                existing.UndoAvailableUntilUtc == undoAvailableUntilUtc)
            {
                return;
            }

            throw new PersistenceConflictException("persistence.receipt_conflict");
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO receipts " +
            "(receipt_id, execution_id, receipt_kind, receipt_json, created_utc, " +
            "undo_available_until_utc) VALUES " +
            "($receipt, $execution, $kind, $json, $created, $undo);";
        command.Parameters.AddWithValue(
            "$receipt",
            SqliteStorePrimitives.FormatGuid(receiptId.Value));
        command.Parameters.AddWithValue(
            "$execution",
            SqliteStorePrimitives.FormatGuid(executionId.Value));
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue(
            "$created",
            SqliteStorePrimitives.FormatUtc(createdUtc));
        command.Parameters.AddWithValue(
            "$undo",
            undoAvailableUntilUtc is null
                ? DBNull.Value
                : SqliteStorePrimitives.FormatUtc(undoAvailableUntilUtc.Value));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new PersistenceConflictException(
                "persistence.receipt_insert_failed");
        }
    }

    private static async Task CompleteExecutionAsync(
        SqliteConnection connection,
        ExecutionId executionId,
        DateTimeOffset completedUtc,
        int verifiedStepCount,
        CancellationToken cancellationToken)
    {
        string summary = JsonSerializer.Serialize(
            new
            {
                contractVersion = "tooltail.execution-verification/1",
                verifiedStepCount,
            });
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "UPDATE executions SET completed_utc = $completed, status = 'verified', " +
            "verification_summary_json = $summary, residual_effects_json = '[]' " +
            "WHERE execution_id = $execution AND " +
            "(completed_utc IS NULL OR (completed_utc = $completed AND status = 'verified'));";
        command.Parameters.AddWithValue(
            "$completed",
            SqliteStorePrimitives.FormatUtc(completedUtc));
        command.Parameters.AddWithValue("$summary", summary);
        command.Parameters.AddWithValue(
            "$execution",
            SqliteStorePrimitives.FormatGuid(executionId.Value));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new PersistenceConflictException(
                "persistence.execution_completion_conflict");
        }
    }

    private static async Task<PersistedReceipt?> ReadReceiptAsync(
        SqliteConnection connection,
        ExecutionId executionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT receipt_id, receipt_kind, receipt_json, created_utc, " +
            "undo_available_until_utc, e.completed_utc, e.status, " +
            "e.verification_summary_json, e.residual_effects_json " +
            "FROM receipts r JOIN executions e ON e.execution_id = r.execution_id " +
            "WHERE r.execution_id = $execution;";
        command.Parameters.AddWithValue(
            "$execution",
            SqliteStorePrimitives.FormatGuid(executionId.Value));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        string json = reader.GetString(2);
        DateTimeOffset createdUtc = SqliteStorePrimitives.ParseUtc(reader.GetString(3));
        if (reader.IsDBNull(5) ||
            SqliteStorePrimitives.ParseUtc(reader.GetString(5)) != createdUtc ||
            !string.Equals(reader.GetString(6), "verified", StringComparison.Ordinal) ||
            reader.IsDBNull(7) ||
            reader.IsDBNull(8) ||
            !string.Equals(reader.GetString(8), "[]", StringComparison.Ordinal) ||
            !HasValidVerificationSummary(json, reader.GetString(7)))
        {
            throw new FormatException(
                "Persisted receipt and execution projections are inconsistent.");
        }

        return new PersistedReceipt(
            new ReceiptId(Guid.Parse(reader.GetString(0))),
            reader.GetString(1),
            json,
            createdUtc,
            reader.IsDBNull(4)
                ? null
                : SqliteStorePrimitives.ParseUtc(reader.GetString(4)));
    }

    private static bool HasValidVerificationSummary(
        string receiptJson,
        string summaryJson)
    {
        if (!SqliteStorePrimitives.IsValidJson(receiptJson) ||
            !SqliteStorePrimitives.IsValidJson(summaryJson))
        {
            return false;
        }

        using JsonDocument receipt = JsonDocument.Parse(receiptJson);
        if (!receipt.RootElement.TryGetProperty(
                "verifiedSteps",
                out JsonElement verifiedSteps) ||
            verifiedSteps.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        string expected = JsonSerializer.Serialize(
            new
            {
                contractVersion = "tooltail.execution-verification/1",
                verifiedStepCount = verifiedSteps.GetArrayLength(),
            });
        return string.Equals(summaryJson, expected, StringComparison.Ordinal);
    }

    private static async Task<PersistedPlanContext?> LoadPlanContextAsync(
        SqliteConnection connection,
        PlanId planId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT plan_kind, plan_json, plan_fingerprint, status, " +
            "p.original_execution_id, p.original_plan_id, p.original_plan_fingerprint, " +
            "p.plan_contract_version, v.skill_id, v.version_number, p.grant_id, " +
            "v.spec_hash, g.root_identity, g.capabilities_json, s.companion_id, " +
            "g.companion_id FROM execution_plans p " +
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
            return null;
        }

        return new PersistedPlanContext(
            reader.GetString(0),
            reader.GetString(1),
            new PlanFingerprint(reader.GetString(2)),
            reader.GetString(3),
            reader.IsDBNull(4)
                ? null
                : new ExecutionId(Guid.Parse(reader.GetString(4))),
            reader.IsDBNull(5)
                ? null
                : new PlanId(Guid.Parse(reader.GetString(5))),
            reader.IsDBNull(6)
                ? null
                : new PlanFingerprint(reader.GetString(6)),
            reader.GetString(7),
            new SkillId(Guid.Parse(reader.GetString(8))),
            new Tooltail.Domain.Skills.SkillVersionNumber(reader.GetInt32(9)),
            new GrantId(Guid.Parse(reader.GetString(10))),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.GetString(15));
    }

    private static bool PlanMatchesJournal(
        PersistedPlanContext plan,
        ExecutionJournal journal)
    {
        if (plan.Fingerprint != journal.PlanFingerprint ||
            !SqliteStorePrimitives.IsValidJson(plan.CanonicalJson) ||
            !SqliteStorePrimitives.IsValidJson(plan.CapabilitiesJson) ||
            !SqliteStorePrimitives.HashMatches(
                plan.CanonicalJson,
                plan.Fingerprint.Value) ||
            !string.Equals(
                plan.Kind,
                journal.Kind == ExecutionJournalKind.Standard
                    ? "standard"
                    : "recovery",
                StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(plan.CanonicalJson);
            JsonElement root = document.RootElement;
            string expectedContract = journal.Kind == ExecutionJournalKind.Standard
                ? "tooltail.execution-plan/1"
                : "tooltail.recovery-plan/1";
            if (!string.Equals(
                    plan.ContractVersion,
                    expectedContract,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    plan.SkillCompanionId,
                    plan.GrantCompanionId,
                    StringComparison.Ordinal) ||
                !HasString(root, "contractVersion", expectedContract) ||
                !HasString(
                    root,
                    "planId",
                    SqliteStorePrimitives.FormatGuid(journal.PlanId.Value)) ||
                !AuthorityMatchesPlanDocument(root, plan) ||
                !root.TryGetProperty("operations", out JsonElement operations) ||
                operations.ValueKind != JsonValueKind.Array ||
                operations.GetArrayLength() != journal.OperationCount)
            {
                return false;
            }

            int index = 0;
            foreach (JsonElement operation in operations.EnumerateArray())
            {
                if (journal.Kind == ExecutionJournalKind.Standard)
                {
                    if (!HasString(
                            operation,
                            "primitive",
                            SqliteStorePrimitives.ToStorage(
                                journal.OperationPrimitives[index])))
                    {
                        return false;
                    }
                }
                else if (!HasString(
                             operation,
                             "recoveryPrimitive",
                             SqliteStorePrimitives.ToStorage(
                                 journal.RecoveryOperationPrimitives[index])) ||
                         !operation.TryGetProperty(
                             "originalStepSequence",
                             out JsonElement originalStep) ||
                         !originalStep.TryGetInt32(out int persistedStep) ||
                         persistedStep != journal.RecoveryOriginalStepSequences[index])
                {
                    return false;
                }

                index++;
            }

            return journal.Kind == ExecutionJournalKind.Standard
                ? plan.OriginalExecutionId is null &&
                  plan.OriginalPlanId is null &&
                  plan.OriginalPlanFingerprint is null
                : plan.OriginalExecutionId is not null &&
                  plan.OriginalPlanId is not null &&
                  plan.OriginalPlanFingerprint is not null &&
                  root.TryGetProperty(
                      "originalExecution",
                      out JsonElement original) &&
                  HasString(
                      original,
                      "executionId",
                      SqliteStorePrimitives.FormatGuid(
                          plan.OriginalExecutionId.Value.Value)) &&
                  HasString(
                      original,
                      "planId",
                      SqliteStorePrimitives.FormatGuid(
                          plan.OriginalPlanId.Value.Value)) &&
                  HasString(
                      original,
                      "planFingerprint",
                      plan.OriginalPlanFingerprint.Value);
        }
        catch (Exception exception) when (exception is
            ArgumentException or FormatException or JsonException or OverflowException)
        {
            return false;
        }
    }

    private static bool HasString(
        JsonElement parent,
        string propertyName,
        string expected) =>
        parent.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String &&
        string.Equals(value.GetString(), expected, StringComparison.Ordinal);

    private static bool AuthorityMatchesPlanDocument(
        JsonElement root,
        PersistedPlanContext plan)
    {
        if (!root.TryGetProperty("skill", out JsonElement skill) ||
            skill.ValueKind != JsonValueKind.Object ||
            !HasString(
                skill,
                "id",
                SqliteStorePrimitives.FormatGuid(plan.SkillId.Value)) ||
            !skill.TryGetProperty("version", out JsonElement version) ||
            !version.TryGetInt32(out int persistedVersion) ||
            persistedVersion != plan.SkillVersion.Value ||
            !HasString(skill, "specificationSha256", plan.SpecificationHash) ||
            !root.TryGetProperty("grant", out JsonElement grant) ||
            grant.ValueKind != JsonValueKind.Object ||
            !HasString(
                grant,
                "id",
                SqliteStorePrimitives.FormatGuid(plan.GrantId.Value)) ||
            !HasString(grant, "rootIdentity", plan.RootIdentity) ||
            !grant.TryGetProperty("actions", out JsonElement actions) ||
            actions.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        using JsonDocument capabilities = JsonDocument.Parse(plan.CapabilitiesJson);
        if (capabilities.RootElement.ValueKind != JsonValueKind.Array ||
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

    private static async Task<ExistingExecutionMatch> MatchExistingExecutionAsync(
        SqliteConnection connection,
        ExecutionJournal expected,
        PlanApproval approval,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT e.approval_id, a.consumed_utc " +
            "FROM executions e JOIN approvals a ON a.approval_id = e.approval_id " +
            "WHERE e.execution_id = $execution;";
        command.Parameters.AddWithValue(
            "$execution",
            SqliteStorePrimitives.FormatGuid(expected.ExecutionId.Value));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return ExistingExecutionMatch.Missing;
        }

        string persistedApprovalId = reader.GetString(0);
        string? consumedUtc = reader.IsDBNull(1) ? null : reader.GetString(1);
        await reader.DisposeAsync().ConfigureAwait(false);
        if (!string.Equals(
                persistedApprovalId,
                SqliteStorePrimitives.FormatGuid(approval.Id.Value),
                StringComparison.Ordinal) ||
            !string.Equals(
                consumedUtc,
                SqliteStorePrimitives.FormatUtc(approval.ConsumedUtc!.Value),
                StringComparison.Ordinal))
        {
            return ExistingExecutionMatch.Conflict;
        }

        DomainResult<ExecutionJournal> loaded = await LoadValidatedJournalAsync(
            connection,
            expected.ExecutionId,
            cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return ExistingExecutionMatch.Conflict;
        }

        ExecutionJournal persisted = loaded.Value!;
        bool matches = persisted.PlanId == expected.PlanId &&
            persisted.PlanFingerprint == expected.PlanFingerprint &&
            persisted.Kind == expected.Kind &&
            persisted.OperationPrimitives.SequenceEqual(expected.OperationPrimitives) &&
            persisted.OperationInverseKinds.SequenceEqual(expected.OperationInverseKinds) &&
            persisted.RecoveryOperationPrimitives.SequenceEqual(
                expected.RecoveryOperationPrimitives) &&
            persisted.RecoveryOriginalStepSequences.SequenceEqual(
                expected.RecoveryOriginalStepSequences) &&
            persisted.Events.Count >= 1 &&
            SqliteJournalCodec.Equivalent(persisted.Events[0], expected.Events[0]);
        return matches
            ? ExistingExecutionMatch.Exact
            : ExistingExecutionMatch.Conflict;
    }

    private static async Task<ExecutionId[]> ReadUnreceiptedExecutionIdsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        List<ExecutionId> ids = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT e.execution_id FROM executions e " +
            "LEFT JOIN receipts r ON r.execution_id = e.execution_id " +
            "WHERE r.execution_id IS NULL ORDER BY e.started_utc, e.execution_id " +
            "LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", MaximumRecoveryCandidates + 1);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(new ExecutionId(Guid.Parse(reader.GetString(0))));
        }

        return ids.ToArray();
    }

    private static async Task<DomainResult<ExecutionJournal>> LoadValidatedJournalAsync(
        SqliteConnection connection,
        ExecutionId executionId,
        CancellationToken cancellationToken)
    {
        DomainResult<ExecutionJournal> loaded =
            await SqliteJournalCodec.LoadJournalAsync(
                connection,
                executionId,
                cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded;
        }

        PersistedPlanContext? plan = await LoadPlanContextAsync(
            connection,
            loaded.Value!.PlanId,
            cancellationToken).ConfigureAwait(false);
        return plan is not null && PlanMatchesJournal(plan, loaded.Value)
            ? loaded
            : DomainResult.Failure<ExecutionJournal>(
                "persistence.journal_plan_invalid",
                "Persisted journal authority does not match its canonical plan.");
    }

    private static bool IsExpectedStoreFailure(Exception exception) =>
        exception is PersistenceConflictException or SqliteException or IOException or
            UnauthorizedAccessException or System.Security.SecurityException or
            ArgumentException or FormatException or JsonException or
            InvalidOperationException or OverflowException;

    private enum ExistingExecutionMatch
    {
        Missing,
        Exact,
        Conflict,
    }

    private sealed record PersistedPlanContext(
        string Kind,
        string CanonicalJson,
        PlanFingerprint Fingerprint,
        string Status,
        ExecutionId? OriginalExecutionId,
        PlanId? OriginalPlanId,
        PlanFingerprint? OriginalPlanFingerprint,
        string ContractVersion,
        SkillId SkillId,
        Tooltail.Domain.Skills.SkillVersionNumber SkillVersion,
        GrantId GrantId,
        string SpecificationHash,
        string RootIdentity,
        string CapabilitiesJson,
        string SkillCompanionId,
        string GrantCompanionId);

    private sealed record PersistedReceipt(
        ReceiptId Id,
        string Kind,
        string Json,
        DateTimeOffset CreatedUtc,
        DateTimeOffset? UndoAvailableUntilUtc);
}
