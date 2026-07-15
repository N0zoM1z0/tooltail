using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Tooltail.Domain.Common;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Infrastructure.Sqlite;

internal static class SqliteReceiptCodec
{
    public const string StandardContractVersion = "tooltail.execution-receipt/1";
    public const string RecoveryContractVersion = "tooltail.recovery-receipt/1";

    private const int MaximumEvidenceSteps = 10_000;

    public static bool IsWithinBounds(ExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.VerifiedSteps.Count > MaximumEvidenceSteps ||
            receipt.ResidualEffectCodes.Count > MaximumEvidenceSteps)
        {
            return false;
        }

        long estimatedBytes = 1_024L + (receipt.VerifiedSteps.Count * 512L);
        foreach (string residual in receipt.ResidualEffectCodes)
        {
            if (!AddBoundedString(residual, ref estimatedBytes, 128))
            {
                return false;
            }
        }

        foreach (VerifiedStepEvidence step in receipt.VerifiedSteps)
        {
            if (!AddBoundedString(step.SourceRelativePath, ref estimatedBytes, 1_024) ||
                !AddBoundedString(
                    step.DestinationRelativePath,
                    ref estimatedBytes,
                    1_024) ||
                !AddEntryStrings(step.Destination, ref estimatedBytes))
            {
                return false;
            }
        }

        return estimatedBytes <= SqliteStorePrimitives.MaximumJsonBytes;
    }

    public static bool IsWithinBounds(RecoveryExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.VerifiedSteps.Count > MaximumEvidenceSteps ||
            receipt.ResidualEffectCodes.Count > MaximumEvidenceSteps)
        {
            return false;
        }

        long estimatedBytes = 1_024L + (receipt.VerifiedSteps.Count * 512L);
        foreach (string residual in receipt.ResidualEffectCodes)
        {
            if (!AddBoundedString(residual, ref estimatedBytes, 128))
            {
                return false;
            }
        }

        foreach (VerifiedRecoveryStepEvidence step in receipt.VerifiedSteps)
        {
            if (!AddBoundedString(
                    step.SourceRelativePath,
                    ref estimatedBytes,
                    1_024) ||
                !AddBoundedString(
                    step.DestinationRelativePath,
                    ref estimatedBytes,
                    1_024) ||
                !AddEntryStrings(step.RecoveredEntry, ref estimatedBytes))
            {
                return false;
            }
        }

        return estimatedBytes <= SqliteStorePrimitives.MaximumJsonBytes;
    }

    public static string Encode(ExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArrayBufferWriter<byte> buffer = new();
        using Utf8JsonWriter writer = CreateWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("contractVersion", StandardContractVersion);
        writer.WriteString("receiptId", Format(receipt.Id.Value));
        writer.WriteString("executionId", Format(receipt.ExecutionId.Value));
        writer.WriteString("planId", Format(receipt.PlanId.Value));
        writer.WriteString("planFingerprint", receipt.PlanFingerprint.Value);
        writer.WriteString("completedUtc", Format(receipt.CompletedUtc));
        writer.WriteNumber("verifiedStepCount", receipt.VerifiedStepCount);
        WriteOptionalUtc(writer, "undoAvailableUntilUtc", receipt.UndoAvailableUntilUtc);
        WriteStrings(writer, "residualEffectCodes", receipt.ResidualEffectCodes);
        writer.WritePropertyName("verifiedSteps");
        writer.WriteStartArray();
        foreach (VerifiedStepEvidence step in receipt.VerifiedSteps)
        {
            WriteStep(writer, step);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string Encode(RecoveryExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArrayBufferWriter<byte> buffer = new();
        using Utf8JsonWriter writer = CreateWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("contractVersion", RecoveryContractVersion);
        writer.WriteString("receiptId", Format(receipt.Id.Value));
        writer.WriteString("executionId", Format(receipt.ExecutionId.Value));
        writer.WriteString("planId", Format(receipt.PlanId.Value));
        writer.WriteString("planFingerprint", receipt.PlanFingerprint.Value);
        writer.WriteString("originalExecutionId", Format(receipt.OriginalExecutionId.Value));
        writer.WriteString("originalPlanId", Format(receipt.OriginalPlanId.Value));
        writer.WriteString(
            "originalPlanFingerprint",
            receipt.OriginalPlanFingerprint.Value);
        writer.WriteString("completedUtc", Format(receipt.CompletedUtc));
        WriteStrings(writer, "residualEffectCodes", receipt.ResidualEffectCodes);
        writer.WritePropertyName("verifiedSteps");
        writer.WriteStartArray();
        foreach (VerifiedRecoveryStepEvidence step in receipt.VerifiedSteps)
        {
            WriteRecoveryStep(writer, step);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static DomainResult<ExecutionReceipt> DecodeStandard(
        string json,
        ReceiptId expectedReceiptId,
        ExecutionJournal journal,
        DateTimeOffset expectedCompletedUtc,
        DateTimeOffset? expectedUndoAvailableUntilUtc,
        string canonicalPlanJson)
    {
        ArgumentNullException.ThrowIfNull(journal);
        try
        {
            using JsonDocument document = Parse(json);
            JsonElement root = document.RootElement;
            RequireProperties(
                root,
                "contractVersion",
                "receiptId",
                "executionId",
                "planId",
                "planFingerprint",
                "completedUtc",
                "verifiedStepCount",
                "undoAvailableUntilUtc",
                "residualEffectCodes",
                "verifiedSteps");
            RequireString(root, "contractVersion", StandardContractVersion);
            RequireGuid(root, "receiptId", expectedReceiptId.Value);
            RequireGuid(root, "executionId", journal.ExecutionId.Value);
            RequireGuid(root, "planId", journal.PlanId.Value);
            RequireString(root, "planFingerprint", journal.PlanFingerprint.Value);
            RequireUtc(root, "completedUtc", expectedCompletedUtc);
            int verifiedStepCount = GetInt32(root, "verifiedStepCount");
            DateTimeOffset? undoAvailableUntilUtc = GetOptionalUtc(
                root,
                "undoAvailableUntilUtc");
            if (verifiedStepCount != journal.OperationCount ||
                undoAvailableUntilUtc != expectedUndoAvailableUntilUtc)
            {
                return Failure<ExecutionReceipt>("persistence.receipt_corrupt");
            }

            string[] residuals = ReadStrings(root, "residualEffectCodes");
            VerifiedStepEvidence[] steps = ReadStandardSteps(root, "verifiedSteps");
            if (!StandardEvidenceMatchesPlan(steps, canonicalPlanJson))
            {
                return Failure<ExecutionReceipt>("persistence.receipt_plan_mismatch");
            }

            return ExecutionReceipt.RehydrateVerified(
                expectedReceiptId,
                journal,
                expectedCompletedUtc,
                undoAvailableUntilUtc,
                residuals,
                steps);
        }
        catch (Exception exception) when (IsMalformed(exception))
        {
            return Failure<ExecutionReceipt>("persistence.receipt_corrupt");
        }
    }

    public static DomainResult<RecoveryExecutionReceipt> DecodeRecovery(
        string json,
        ReceiptId expectedReceiptId,
        ExecutionJournal recoveryJournal,
        ExecutionJournal originalJournal,
        DateTimeOffset expectedCompletedUtc,
        string canonicalPlanJson)
    {
        ArgumentNullException.ThrowIfNull(recoveryJournal);
        ArgumentNullException.ThrowIfNull(originalJournal);
        try
        {
            using JsonDocument document = Parse(json);
            JsonElement root = document.RootElement;
            RequireProperties(
                root,
                "contractVersion",
                "receiptId",
                "executionId",
                "planId",
                "planFingerprint",
                "originalExecutionId",
                "originalPlanId",
                "originalPlanFingerprint",
                "completedUtc",
                "residualEffectCodes",
                "verifiedSteps");
            RequireString(root, "contractVersion", RecoveryContractVersion);
            RequireGuid(root, "receiptId", expectedReceiptId.Value);
            RequireGuid(root, "executionId", recoveryJournal.ExecutionId.Value);
            RequireGuid(root, "planId", recoveryJournal.PlanId.Value);
            RequireString(
                root,
                "planFingerprint",
                recoveryJournal.PlanFingerprint.Value);
            RequireGuid(
                root,
                "originalExecutionId",
                originalJournal.ExecutionId.Value);
            RequireGuid(root, "originalPlanId", originalJournal.PlanId.Value);
            RequireString(
                root,
                "originalPlanFingerprint",
                originalJournal.PlanFingerprint.Value);
            RequireUtc(root, "completedUtc", expectedCompletedUtc);
            string[] residuals = ReadStrings(root, "residualEffectCodes");
            VerifiedRecoveryStepEvidence[] steps = ReadRecoverySteps(
                root,
                "verifiedSteps");
            if (!RecoveryEvidenceMatchesPlan(steps, canonicalPlanJson))
            {
                return Failure<RecoveryExecutionReceipt>(
                    "persistence.receipt_plan_mismatch");
            }

            return RecoveryExecutionReceipt.RehydrateVerified(
                expectedReceiptId,
                recoveryJournal,
                originalJournal,
                originalJournal.ExecutionId,
                originalJournal.PlanId,
                originalJournal.PlanFingerprint,
                expectedCompletedUtc,
                steps,
                residuals);
        }
        catch (Exception exception) when (IsMalformed(exception))
        {
            return Failure<RecoveryExecutionReceipt>("persistence.receipt_corrupt");
        }
    }

    private static void WriteStep(Utf8JsonWriter writer, VerifiedStepEvidence step)
    {
        writer.WriteStartObject();
        writer.WriteNumber("stepSequence", step.StepSequence);
        writer.WriteString("primitive", SqliteStorePrimitives.ToStorage(step.Primitive));
        WriteOptionalString(writer, "sourceRelativePath", step.SourceRelativePath);
        writer.WriteString("destinationRelativePath", step.DestinationRelativePath);
        writer.WriteBoolean("destinationWasAbsent", step.DestinationWasAbsent);
        writer.WritePropertyName("destination");
        WriteEntry(writer, step.Destination);
        writer.WriteEndObject();
    }

    private static void WriteRecoveryStep(
        Utf8JsonWriter writer,
        VerifiedRecoveryStepEvidence step)
    {
        writer.WriteStartObject();
        writer.WriteNumber("stepSequence", step.StepSequence);
        writer.WriteNumber("originalStepSequence", step.OriginalStepSequence);
        writer.WriteString("primitive", SqliteStorePrimitives.ToStorage(step.Primitive));
        writer.WriteString("sourceRelativePath", step.SourceRelativePath);
        WriteOptionalString(writer, "destinationRelativePath", step.DestinationRelativePath);
        writer.WritePropertyName("recoveredEntry");
        WriteEntry(writer, step.RecoveredEntry);
        writer.WriteEndObject();
    }

    private static void WriteEntry(Utf8JsonWriter writer, VerifiedEntryEvidence entry)
    {
        writer.WriteStartObject();
        writer.WriteString(
            "kind",
            entry.Kind == VerifiedEntryKind.File ? "file" : "directory");
        writer.WriteString("volumeIdentity", entry.VolumeIdentity);
        writer.WriteString("entryIdentity", entry.EntryIdentity);
        if (entry.Length is null)
        {
            writer.WriteNull("length");
        }
        else
        {
            writer.WriteNumber("length", entry.Length.Value);
        }

        writer.WriteString("creationUtc", Format(entry.CreationUtc));
        writer.WriteString("lastWriteUtc", Format(entry.LastWriteUtc));
        writer.WriteNumber("attributes", entry.Attributes);
        WriteOptionalString(writer, "contentSha256", entry.ContentHash?.Value);
        writer.WriteEndObject();
    }

    private static VerifiedStepEvidence[] ReadStandardSteps(
        JsonElement root,
        string propertyName)
    {
        JsonElement array = GetArray(root, propertyName);
        EnsureArrayBound(array);
        return array.EnumerateArray().Select(ReadStandardStep).ToArray();
    }

    private static VerifiedStepEvidence ReadStandardStep(JsonElement element)
    {
        RequireProperties(
            element,
            "stepSequence",
            "primitive",
            "sourceRelativePath",
            "destinationRelativePath",
            "destinationWasAbsent",
            "destination");
        return new VerifiedStepEvidence(
            GetInt32(element, "stepSequence"),
            SqliteStorePrimitives.ParseFilePrimitive(GetString(element, "primitive")),
            GetOptionalString(element, "sourceRelativePath"),
            GetString(element, "destinationRelativePath"),
            GetBoolean(element, "destinationWasAbsent"),
            ReadEntry(GetObject(element, "destination")));
    }

    private static VerifiedRecoveryStepEvidence[] ReadRecoverySteps(
        JsonElement root,
        string propertyName)
    {
        JsonElement array = GetArray(root, propertyName);
        EnsureArrayBound(array);
        return array.EnumerateArray().Select(ReadRecoveryStep).ToArray();
    }

    private static VerifiedRecoveryStepEvidence ReadRecoveryStep(JsonElement element)
    {
        RequireProperties(
            element,
            "stepSequence",
            "originalStepSequence",
            "primitive",
            "sourceRelativePath",
            "destinationRelativePath",
            "recoveredEntry");
        return new VerifiedRecoveryStepEvidence(
            GetInt32(element, "stepSequence"),
            GetInt32(element, "originalStepSequence"),
            SqliteStorePrimitives.ParseRecoveryPrimitive(GetString(element, "primitive")),
            GetString(element, "sourceRelativePath"),
            GetOptionalString(element, "destinationRelativePath"),
            ReadEntry(GetObject(element, "recoveredEntry")));
    }

    private static VerifiedEntryEvidence ReadEntry(JsonElement element)
    {
        RequireProperties(
            element,
            "kind",
            "volumeIdentity",
            "entryIdentity",
            "length",
            "creationUtc",
            "lastWriteUtc",
            "attributes",
            "contentSha256");
        VerifiedEntryKind kind = GetString(element, "kind") switch
        {
            "file" => VerifiedEntryKind.File,
            "directory" => VerifiedEntryKind.Directory,
            _ => throw new FormatException("Unknown verified entry kind."),
        };
        return new VerifiedEntryEvidence(
            kind,
            GetString(element, "volumeIdentity"),
            GetString(element, "entryIdentity"),
            GetOptionalInt64(element, "length"),
            SqliteStorePrimitives.ParseUtc(GetString(element, "creationUtc")),
            SqliteStorePrimitives.ParseUtc(GetString(element, "lastWriteUtc")),
            GetInt32(element, "attributes"),
            GetOptionalString(element, "contentSha256") is { } digest
                ? new ContentHash(digest)
                : null);
    }

    private static bool StandardEvidenceMatchesPlan(
        VerifiedStepEvidence[] steps,
        string canonicalPlanJson)
    {
        using JsonDocument plan = Parse(canonicalPlanJson);
        JsonElement operations = GetArray(plan.RootElement, "operations");
        if (operations.GetArrayLength() != steps.Length)
        {
            return false;
        }

        int index = 0;
        foreach (JsonElement operation in operations.EnumerateArray())
        {
            VerifiedStepEvidence step = steps[index++];
            if (GetInt32(operation, "sequence") != step.StepSequence ||
                !string.Equals(
                    GetString(operation, "primitive"),
                    SqliteStorePrimitives.ToStorage(step.Primitive),
                    StringComparison.Ordinal) ||
                !string.Equals(
                    GetOptionalString(operation, "sourceRelativePath"),
                    step.SourceRelativePath,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    GetString(operation, "destinationRelativePath"),
                    step.DestinationRelativePath,
                    StringComparison.Ordinal) ||
                step.DestinationWasAbsent != string.Equals(
                    GetString(operation, "destinationPrecondition"),
                    "absent",
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RecoveryEvidenceMatchesPlan(
        VerifiedRecoveryStepEvidence[] steps,
        string canonicalPlanJson)
    {
        using JsonDocument plan = Parse(canonicalPlanJson);
        JsonElement operations = GetArray(plan.RootElement, "operations");
        if (operations.GetArrayLength() != steps.Length)
        {
            return false;
        }

        int index = 0;
        foreach (JsonElement operation in operations.EnumerateArray())
        {
            VerifiedRecoveryStepEvidence step = steps[index++];
            if (GetInt32(operation, "sequence") != step.StepSequence ||
                GetInt32(operation, "originalStepSequence") !=
                    step.OriginalStepSequence ||
                !string.Equals(
                    GetString(operation, "recoveryPrimitive"),
                    SqliteStorePrimitives.ToStorage(step.Primitive),
                    StringComparison.Ordinal) ||
                !string.Equals(
                    GetString(operation, "sourceRelativePath"),
                    step.SourceRelativePath,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    GetOptionalString(operation, "destinationRelativePath"),
                    step.DestinationRelativePath,
                    StringComparison.Ordinal) ||
                ReadEntry(GetObject(operation, "expectedSource")) !=
                    step.RecoveredEntry)
            {
                return false;
            }
        }

        return true;
    }

    private static JsonDocument Parse(string json)
    {
        if (!SqliteStorePrimitives.IsValidJson(json))
        {
            throw new FormatException("Persisted receipt JSON is invalid.");
        }

        JsonDocument document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new FormatException("Persisted receipt is not an object.");
        }

        return document;
    }

    private static void RequireProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            element.EnumerateObject().Count() != names.Length ||
            names.Any(name => !element.TryGetProperty(name, out _)))
        {
            throw new FormatException("Persisted receipt object shape is invalid.");
        }
    }

    private static JsonElement GetObject(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : throw new FormatException("Persisted receipt object is missing.");

    private static JsonElement GetArray(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Array
            ? value
            : throw new FormatException("Persisted receipt array is missing.");

    private static string GetString(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new FormatException("Persisted receipt string is missing.");

    private static string? GetOptionalString(
        JsonElement parent,
        string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value))
        {
            throw new FormatException("Persisted receipt value is missing.");
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw new FormatException("Persisted receipt optional string is invalid."),
        };
    }

    private static int GetInt32(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int parsed)
            ? parsed
            : throw new FormatException("Persisted receipt integer is invalid.");

    private static long? GetOptionalInt64(
        JsonElement parent,
        string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value))
        {
            throw new FormatException("Persisted receipt value is missing.");
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out long parsed)
                ? parsed
                : throw new FormatException("Persisted receipt integer is invalid.");
    }

    private static bool GetBoolean(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new FormatException("Persisted receipt boolean is invalid.");

    private static DateTimeOffset? GetOptionalUtc(
        JsonElement parent,
        string propertyName) =>
        GetOptionalString(parent, propertyName) is { } value
            ? SqliteStorePrimitives.ParseUtc(value)
            : null;

    private static void RequireString(
        JsonElement parent,
        string propertyName,
        string expected)
    {
        if (!string.Equals(
                GetString(parent, propertyName),
                expected,
                StringComparison.Ordinal))
        {
            throw new FormatException("Persisted receipt identity is inconsistent.");
        }
    }

    private static void RequireGuid(
        JsonElement parent,
        string propertyName,
        Guid expected) =>
        RequireString(parent, propertyName, Format(expected));

    private static void RequireUtc(
        JsonElement parent,
        string propertyName,
        DateTimeOffset expected) =>
        RequireString(parent, propertyName, Format(expected));

    private static string[] ReadStrings(JsonElement root, string propertyName)
    {
        JsonElement array = GetArray(root, propertyName);
        EnsureArrayBound(array);
        return array.EnumerateArray()
            .Select(static value =>
                value.ValueKind == JsonValueKind.String
                    ? value.GetString()!
                    : throw new FormatException("Persisted receipt string is invalid."))
            .ToArray();
    }

    private static void EnsureArrayBound(JsonElement array)
    {
        if (array.GetArrayLength() > MaximumEvidenceSteps)
        {
            throw new FormatException("Persisted receipt exceeds its step bound.");
        }
    }

    private static void WriteStrings(
        Utf8JsonWriter writer,
        string propertyName,
        IEnumerable<string> values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (string value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteOptionalString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static void WriteOptionalUtc(
        Utf8JsonWriter writer,
        string propertyName,
        DateTimeOffset? value) =>
        WriteOptionalString(writer, propertyName, value is null ? null : Format(value.Value));

    private static Utf8JsonWriter CreateWriter(IBufferWriter<byte> buffer) =>
        new(
            buffer,
            new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.Default,
                Indented = false,
                SkipValidation = false,
            });

    private static string Format(Guid value) =>
        SqliteStorePrimitives.FormatGuid(value);

    private static string Format(DateTimeOffset value) =>
        SqliteStorePrimitives.FormatUtc(value);

    private static bool IsMalformed(Exception exception) =>
        exception is ArgumentException or FormatException or JsonException or
            InvalidOperationException or OverflowException;

    private static DomainResult<T> Failure<T>(string reasonCode) =>
        DomainResult.Failure<T>(
            reasonCode,
            "Persisted receipt data cannot be rehydrated safely.");

    private static bool AddEntryStrings(
        VerifiedEntryEvidence entry,
        ref long estimatedBytes) =>
        AddBoundedString(entry.VolumeIdentity, ref estimatedBytes, 1_024) &&
        AddBoundedString(entry.EntryIdentity, ref estimatedBytes, 1_024) &&
        AddBoundedString(entry.ContentHash?.Value, ref estimatedBytes, 64);

    private static bool AddBoundedString(
        string? value,
        ref long estimatedBytes,
        int maximumCharacters)
    {
        if (value is null)
        {
            return true;
        }

        if (value.Length > maximumCharacters)
        {
            return false;
        }

        estimatedBytes += Encoding.UTF8.GetByteCount(value);
        return estimatedBytes <= SqliteStorePrimitives.MaximumJsonBytes;
    }
}
