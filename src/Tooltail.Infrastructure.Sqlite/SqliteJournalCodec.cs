using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tooltail.Domain.Common;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Infrastructure.Sqlite;

internal static class SqliteJournalCodec
{
    private const int MaximumJournalEvents = 100_000;

    public static string FilePrimitivesJson(IEnumerable<FilePrimitive> values) =>
        JsonSerializer.Serialize(values.Select(SqliteStorePrimitives.ToStorage));

    public static string InverseKindsJson(IEnumerable<JournalInverseKind> values) =>
        JsonSerializer.Serialize(values.Select(SqliteStorePrimitives.ToStorage));

    public static string RecoveryPrimitivesJson(IEnumerable<RecoveryPrimitive> values) =>
        JsonSerializer.Serialize(values.Select(SqliteStorePrimitives.ToStorage));

    public static string OriginalStepsJson(IEnumerable<int> values) =>
        JsonSerializer.Serialize(values);

    public static async Task InsertEventAsync(
        SqliteConnection connection,
        ExecutionJournalEvent journalEvent,
        CancellationToken cancellationToken)
    {
        EventColumns columns = ToColumns(journalEvent);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO execution_journal_events " +
            "(journal_event_id, execution_id, event_sequence, step_sequence, event_type, " +
            "event_version, occurred_utc, primitive_type, recovery_primitive_type, " +
            "original_step_sequence, precondition_fingerprint, inverse_kind, reason_code, " +
            "recovery_execution_id) VALUES " +
            "($id, $execution, $sequence, $step, $type, 1, $occurred, $primitive, " +
            "$recovery_primitive, $original_step, $fingerprint, $inverse, $reason, " +
            "$recovery_execution);";
        command.Parameters.AddWithValue("$id", EventId(journalEvent));
        command.Parameters.AddWithValue(
            "$execution",
            SqliteStorePrimitives.FormatGuid(journalEvent.ExecutionId.Value));
        command.Parameters.AddWithValue("$sequence", journalEvent.EventSequence);
        command.Parameters.AddWithValue(
            "$step",
            journalEvent.StepSequence is null
                ? DBNull.Value
                : journalEvent.StepSequence.Value);
        command.Parameters.AddWithValue("$type", columns.EventType);
        command.Parameters.AddWithValue(
            "$occurred",
            SqliteStorePrimitives.FormatUtc(journalEvent.OccurredUtc));
        command.Parameters.AddWithValue(
            "$primitive",
            (object?)columns.Primitive ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$recovery_primitive",
            (object?)columns.RecoveryPrimitive ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$original_step",
            columns.OriginalStepSequence is null
                ? DBNull.Value
                : columns.OriginalStepSequence.Value);
        command.Parameters.AddWithValue(
            "$fingerprint",
            (object?)columns.PreconditionFingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$inverse",
            (object?)columns.InverseKind ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$reason",
            (object?)columns.ReasonCode ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$recovery_execution",
            (object?)columns.RecoveryExecutionId ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new PersistenceConflictException("persistence.journal_insert_failed");
        }
    }

    public static async Task<DomainResult<ExecutionJournal>> LoadJournalAsync(
        SqliteConnection connection,
        ExecutionId executionId,
        CancellationToken cancellationToken)
    {
        try
        {
            JournalHeader? header = await LoadHeaderAsync(
                connection,
                executionId,
                cancellationToken).ConfigureAwait(false);
            if (header is null)
            {
                return DomainResult.Failure<ExecutionJournal>(
                    "persistence.journal_not_found",
                    "The requested journal does not exist.");
            }

            FilePrimitive[] filePrimitives = ParseStringArray(
                header.OperationPrimitivesJson,
                SqliteStorePrimitives.ParseFilePrimitive);
            JournalInverseKind[] inverseKinds = ParseStringArray(
                header.OperationInverseKindsJson,
                SqliteStorePrimitives.ParseInverseKind);
            RecoveryPrimitive[] recoveryPrimitives = ParseStringArray(
                header.RecoveryPrimitivesJson,
                SqliteStorePrimitives.ParseRecoveryPrimitive);
            int[] originalSteps = ParseIntArray(header.RecoveryOriginalStepsJson);
            ExecutionJournalEvent[] events = await LoadEventsAsync(
                connection,
                header,
                cancellationToken).ConfigureAwait(false);
            return ExecutionJournal.Rehydrate(
                header.ExecutionId,
                header.PlanId,
                header.PlanFingerprint,
                header.Kind,
                filePrimitives,
                inverseKinds,
                recoveryPrimitives,
                originalSteps,
                events);
        }
        catch (Exception exception) when (exception is
            ArgumentException or FormatException or JsonException or OverflowException)
        {
            return DomainResult.Failure<ExecutionJournal>(
                "persistence.journal_corrupt",
                "Persisted journal data cannot be rehydrated safely.");
        }
    }

    public static async Task<ExecutionJournalEvent?> LoadEventAsync(
        SqliteConnection connection,
        JournalHeader header,
        long eventSequence,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = EventSelectSql +
            " WHERE execution_id = $execution AND event_sequence = $sequence;";
        command.Parameters.AddWithValue(
            "$execution",
            SqliteStorePrimitives.FormatGuid(header.ExecutionId.Value));
        command.Parameters.AddWithValue("$sequence", eventSequence);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? FromReader(reader, header)
            : null;
    }

    public static bool Equivalent(
        ExecutionJournalEvent first,
        ExecutionJournalEvent second) =>
        first == second;

    public static async Task<JournalHeader?> LoadHeaderAsync(
        SqliteConnection connection,
        ExecutionId executionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT e.execution_id, e.plan_id, p.plan_fingerprint, e.journal_kind, " +
            "e.operation_primitives_json, e.operation_inverse_kinds_json, " +
            "e.recovery_primitives_json, e.recovery_original_steps_json, " +
            "p.original_execution_id, p.original_plan_id, p.original_plan_fingerprint " +
            "FROM executions e JOIN execution_plans p ON p.plan_id = e.plan_id " +
            "WHERE e.execution_id = $execution;";
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

        return new JournalHeader(
            new ExecutionId(Guid.Parse(reader.GetString(0))),
            new PlanId(Guid.Parse(reader.GetString(1))),
            new PlanFingerprint(reader.GetString(2)),
            SqliteStorePrimitives.ParseJournalKind(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.IsDBNull(8)
                ? null
                : new ExecutionId(Guid.Parse(reader.GetString(8))),
            reader.IsDBNull(9)
                ? null
                : new PlanId(Guid.Parse(reader.GetString(9))),
            reader.IsDBNull(10)
                ? null
                : new PlanFingerprint(reader.GetString(10)));
    }

    private static async Task<ExecutionJournalEvent[]> LoadEventsAsync(
        SqliteConnection connection,
        JournalHeader header,
        CancellationToken cancellationToken)
    {
        List<ExecutionJournalEvent> events = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = EventSelectSql +
            " WHERE execution_id = $execution ORDER BY event_sequence LIMIT $limit;";
        command.Parameters.AddWithValue(
            "$execution",
            SqliteStorePrimitives.FormatGuid(header.ExecutionId.Value));
        command.Parameters.AddWithValue("$limit", MaximumJournalEvents + 1);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (events.Count == MaximumJournalEvents)
            {
                throw new FormatException("Persisted journal exceeds its event bound.");
            }

            events.Add(FromReader(reader, header));
        }

        return events.ToArray();
    }

    private static ExecutionJournalEvent FromReader(
        SqliteDataReader reader,
        JournalHeader header)
    {
        string eventId = reader.GetString(0);
        long sequence = reader.GetInt64(1);
        int? step = reader.IsDBNull(2) ? null : reader.GetInt32(2);
        string eventType = reader.GetString(3);
        if (reader.GetInt32(4) != 1 ||
            !string.Equals(
                eventId,
                EventId(header.ExecutionId, sequence),
                StringComparison.Ordinal))
        {
            throw new FormatException("Persisted journal event identity is invalid.");
        }

        DateTimeOffset occurred = SqliteStorePrimitives.ParseUtc(reader.GetString(5));
        string? primitive = reader.IsDBNull(6) ? null : reader.GetString(6);
        string? recoveryPrimitive = reader.IsDBNull(7) ? null : reader.GetString(7);
        int? originalStep = reader.IsDBNull(8) ? null : reader.GetInt32(8);
        string? fingerprint = reader.IsDBNull(9) ? null : reader.GetString(9);
        string? inverse = reader.IsDBNull(10) ? null : reader.GetString(10);
        string? reason = reader.IsDBNull(11) ? null : reader.GetString(11);
        string? recoveryExecution = reader.IsDBNull(12) ? null : reader.GetString(12);
        return eventType switch
        {
            "execution_opened" when
                sequence == 1 &&
                step is null &&
                primitive is null &&
                recoveryPrimitive is null &&
                originalStep is null &&
                string.Equals(
                    fingerprint,
                    header.PlanFingerprint.Value,
                    StringComparison.Ordinal) &&
                inverse is null &&
                reason is null &&
                recoveryExecution is null =>
                new ExecutionOpenedEvent(
                    header.ExecutionId,
                    occurred,
                    header.PlanId,
                    header.PlanFingerprint),
            "step_intent" when step is not null && primitive is not null &&
                recoveryPrimitive is null && originalStep is null &&
                fingerprint is not null && inverse is not null &&
                reason is null && recoveryExecution is null =>
                new StepIntentRecordedEvent(
                    header.ExecutionId,
                    sequence,
                    occurred,
                    step.Value,
                    SqliteStorePrimitives.ParseFilePrimitive(primitive),
                    new PlanFingerprint(fingerprint),
                    SqliteStorePrimitives.ParseInverseKind(inverse)),
            "recovery_step_intent" when step is not null &&
                primitive is null && recoveryPrimitive is not null &&
                originalStep is not null && fingerprint is not null &&
                inverse is null && reason is null && recoveryExecution is null =>
                new RecoveryStepIntentRecordedEvent(
                    header.ExecutionId,
                    sequence,
                    occurred,
                    step.Value,
                    SqliteStorePrimitives.ParseRecoveryPrimitive(recoveryPrimitive),
                    originalStep.Value,
                    new PlanFingerprint(fingerprint)),
            "mutation_observed" when step is not null &&
                IsEmptyPayload(
                    primitive,
                    recoveryPrimitive,
                    originalStep,
                    fingerprint,
                    inverse,
                    reason,
                    recoveryExecution) =>
                new StepMutationObservedEvent(
                    header.ExecutionId,
                    sequence,
                    occurred,
                    step.Value),
            "step_committed" when step is not null &&
                IsEmptyPayload(
                    primitive,
                    recoveryPrimitive,
                    originalStep,
                    fingerprint,
                    inverse,
                    reason,
                    recoveryExecution) =>
                new StepCommittedEvent(
                    header.ExecutionId,
                    sequence,
                    occurred,
                    step.Value),
            "step_verified" when step is not null &&
                IsEmptyPayload(
                    primitive,
                    recoveryPrimitive,
                    originalStep,
                    fingerprint,
                    inverse,
                    reason,
                    recoveryExecution) =>
                new StepVerifiedEvent(
                    header.ExecutionId,
                    sequence,
                    occurred,
                    step.Value),
            "step_failed" when step is not null && reason is not null &&
                primitive is null && recoveryPrimitive is null &&
                originalStep is null && fingerprint is null && inverse is null &&
                recoveryExecution is null =>
                new StepFailedEvent(
                    header.ExecutionId,
                    sequence,
                    occurred,
                    step.Value,
                    reason),
            "recovery_required" when step is not null && reason is not null &&
                primitive is null && recoveryPrimitive is null &&
                originalStep is null && fingerprint is null && inverse is null &&
                recoveryExecution is null =>
                new StepRecoveryRequiredEvent(
                    header.ExecutionId,
                    sequence,
                    occurred,
                    step.Value,
                    reason),
            "step_rolled_back" when step is not null &&
                recoveryExecution is not null && primitive is null &&
                recoveryPrimitive is null && originalStep is null &&
                fingerprint is null && inverse is null && reason is null =>
                new StepRolledBackEvent(
                    header.ExecutionId,
                    sequence,
                    occurred,
                    step.Value,
                    new ExecutionId(Guid.Parse(recoveryExecution))),
            _ => throw new FormatException("Persisted journal event shape is invalid."),
        };
    }

    private static EventColumns ToColumns(ExecutionJournalEvent journalEvent) =>
        journalEvent switch
        {
            ExecutionOpenedEvent opened => new(
                "execution_opened",
                null,
                null,
                null,
                opened.PlanFingerprint.Value,
                null,
                null,
                null),
            StepIntentRecordedEvent intent => new(
                "step_intent",
                SqliteStorePrimitives.ToStorage(intent.Primitive),
                null,
                null,
                intent.PreconditionFingerprint.Value,
                SqliteStorePrimitives.ToStorage(intent.InverseKind),
                null,
                null),
            RecoveryStepIntentRecordedEvent intent => new(
                "recovery_step_intent",
                null,
                SqliteStorePrimitives.ToStorage(intent.Primitive),
                intent.OriginalStepSequence,
                intent.PreconditionFingerprint.Value,
                null,
                null,
                null),
            StepMutationObservedEvent => Empty("mutation_observed"),
            StepCommittedEvent => Empty("step_committed"),
            StepVerifiedEvent => Empty("step_verified"),
            StepFailedEvent failed => Empty("step_failed") with
            {
                ReasonCode = failed.FailureCode,
            },
            StepRecoveryRequiredEvent recovery => Empty("recovery_required") with
            {
                ReasonCode = recovery.ReasonCode,
            },
            StepRolledBackEvent rolledBack => Empty("step_rolled_back") with
            {
                RecoveryExecutionId = SqliteStorePrimitives.FormatGuid(
                    rolledBack.RecoveryExecutionId.Value),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(journalEvent)),
        };

    private static EventColumns Empty(string eventType) =>
        new(eventType, null, null, null, null, null, null, null);

    private static string EventId(ExecutionJournalEvent journalEvent) =>
        EventId(journalEvent.ExecutionId, journalEvent.EventSequence);

    private static string EventId(ExecutionId executionId, long eventSequence) =>
        $"{SqliteStorePrimitives.FormatGuid(executionId.Value)}:" +
        eventSequence.ToString("D10", CultureInfo.InvariantCulture);

    private static bool IsEmptyPayload(
        string? primitive,
        string? recoveryPrimitive,
        int? originalStep,
        string? fingerprint,
        string? inverse,
        string? reason,
        string? recoveryExecution) =>
        primitive is null &&
        recoveryPrimitive is null &&
        originalStep is null &&
        fingerprint is null &&
        inverse is null &&
        reason is null &&
        recoveryExecution is null;

    private static T[] ParseStringArray<T>(
        string json,
        Func<string, T> parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        using JsonDocument document = ParseArray(json);
        if (document.RootElement.GetArrayLength() > 10_000)
        {
            throw new FormatException("Persisted operation array exceeds its bound.");
        }

        return document.RootElement.EnumerateArray()
            .Select(element =>
                element.ValueKind == JsonValueKind.String
                    ? parser(element.GetString()!)
                    : throw new FormatException("Persisted operation value is invalid."))
            .ToArray();
    }

    private static int[] ParseIntArray(string json)
    {
        using JsonDocument document = ParseArray(json);
        if (document.RootElement.GetArrayLength() > 10_000)
        {
            throw new FormatException("Persisted operation array exceeds its bound.");
        }

        return document.RootElement.EnumerateArray()
            .Select(element =>
                element.ValueKind == JsonValueKind.Number &&
                element.TryGetInt32(out int value)
                    ? value
                    : throw new FormatException("Persisted step value is invalid."))
            .ToArray();
    }

    private static JsonDocument ParseArray(string json)
    {
        if (!SqliteStorePrimitives.IsValidJson(json))
        {
            throw new FormatException("Persisted operation metadata is invalid JSON.");
        }

        JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            document.Dispose();
            throw new FormatException("Persisted operation metadata is not an array.");
        }

        return document;
    }

    private const string EventSelectSql =
        "SELECT journal_event_id, event_sequence, step_sequence, event_type, " +
        "event_version, occurred_utc, " +
        "primitive_type, recovery_primitive_type, original_step_sequence, " +
        "precondition_fingerprint, inverse_kind, reason_code, recovery_execution_id " +
        "FROM execution_journal_events";

    internal sealed record JournalHeader(
        ExecutionId ExecutionId,
        PlanId PlanId,
        PlanFingerprint PlanFingerprint,
        ExecutionJournalKind Kind,
        string OperationPrimitivesJson,
        string OperationInverseKindsJson,
        string RecoveryPrimitivesJson,
        string RecoveryOriginalStepsJson,
        ExecutionId? OriginalExecutionId,
        PlanId? OriginalPlanId,
        PlanFingerprint? OriginalPlanFingerprint);

    private sealed record EventColumns(
        string EventType,
        string? Primitive,
        string? RecoveryPrimitive,
        int? OriginalStepSequence,
        string? PreconditionFingerprint,
        string? InverseKind,
        string? ReasonCode,
        string? RecoveryExecutionId);
}
