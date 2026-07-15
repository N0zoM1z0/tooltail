using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Domain.Teaching;

namespace Tooltail.Infrastructure.Sqlite;

internal static class SqliteStorePrimitives
{
    public const int MaximumJsonBytes = 4 * 1024 * 1024;
    public const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public static bool IsValidJson(string? json, bool nullable = false)
    {
        if (json is null)
        {
            return nullable;
        }

        int byteCount = Encoding.UTF8.GetByteCount(json);
        if (byteCount is < 2 or > MaximumJsonBytes)
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64,
                });
            return document.RootElement.ValueKind is JsonValueKind.Object or
                JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool HashMatches(string json, string expectedLowerHex) =>
        string.Equals(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))),
            expectedLowerHex,
            StringComparison.Ordinal);

    public static string FormatUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Persisted timestamps must use UTC.", nameof(value));
        }

        return value.UtcDateTime.ToString(UtcFormat, CultureInfo.InvariantCulture);
    }

    public static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.TryParseExact(
            value,
            UtcFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed) &&
        parsed.Offset == TimeSpan.Zero
            ? parsed
            : throw new FormatException("A persisted timestamp is not canonical UTC.");

    public static string FormatGuid(Guid value) => value.ToString("D");

    public static string SkillVersionKey(
        SkillId skillId,
        SkillVersionNumber version) =>
        $"{FormatGuid(skillId.Value)}:v{version.Value}";

    public static string GrantFingerprint(LocalFolderGrant grant)
    {
        ArrayBufferWriter<byte> buffer = new();
        using Utf8JsonWriter writer = new(
            buffer,
            new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.Default,
                Indented = false,
                SkipValidation = false,
            });
        writer.WriteStartObject();
        writer.WriteString("grantId", FormatGuid(grant.Id.Value));
        writer.WriteString("companionId", FormatGuid(grant.CompanionId.Value));
        writer.WriteString("rootIdentity", grant.RootIdentity.Value);
        writer.WritePropertyName("capabilities");
        writer.WriteStartArray();
        foreach (string capability in grant.Capabilities
                     .Select(ToStorage)
                     .Order(StringComparer.Ordinal))
        {
            writer.WriteStringValue(capability);
        }

        writer.WriteEndArray();
        writer.WriteString("issuedUtc", FormatUtc(grant.IssuedAt));
        if (grant.ExpiresAt is null)
        {
            writer.WriteNull("expiresUtc");
        }
        else
        {
            writer.WriteString("expiresUtc", FormatUtc(grant.ExpiresAt.Value));
        }

        writer.WriteEndObject();
        writer.Flush();
        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    public static string CapabilitiesJson(IEnumerable<GrantCapability> capabilities) =>
        JsonSerializer.Serialize(
            capabilities.Select(ToStorage).Order(StringComparer.Ordinal));

    public static string ToStorage(GrantCapability capability) =>
        capability switch
        {
            GrantCapability.Enumerate => "enumerate",
            GrantCapability.ReadMetadata => "read_metadata",
            GrantCapability.ReadContentHash => "read_content_hash",
            GrantCapability.CreateDirectory => "create_directory",
            GrantCapability.Rename => "rename",
            GrantCapability.MoveWithinRoot => "move_within_root",
            GrantCapability.CopyWithinRoot => "copy_within_root",
            _ => throw new ArgumentOutOfRangeException(nameof(capability)),
        };

    public static string ToStorage(FilePrimitive primitive) =>
        primitive switch
        {
            FilePrimitive.EnsureDirectory => "ensure_directory",
            FilePrimitive.RenameFile => "rename_file",
            FilePrimitive.MoveFile => "move_file",
            FilePrimitive.CopyFile => "copy_file",
            _ => throw new ArgumentOutOfRangeException(nameof(primitive)),
        };

    public static FilePrimitive ParseFilePrimitive(string value) =>
        value switch
        {
            "ensure_directory" => FilePrimitive.EnsureDirectory,
            "rename_file" => FilePrimitive.RenameFile,
            "move_file" => FilePrimitive.MoveFile,
            "copy_file" => FilePrimitive.CopyFile,
            _ => throw new FormatException("Unknown persisted file primitive."),
        };

    public static string ToStorage(JournalInverseKind inverse) =>
        inverse switch
        {
            JournalInverseKind.None => "none",
            JournalInverseKind.RenameBack => "rename_back",
            JournalInverseKind.MoveBack => "move_back",
            JournalInverseKind.RemoveCreatedEntry => "remove_created_entry",
            _ => throw new ArgumentOutOfRangeException(nameof(inverse)),
        };

    public static JournalInverseKind ParseInverseKind(string value) =>
        value switch
        {
            "none" => JournalInverseKind.None,
            "rename_back" => JournalInverseKind.RenameBack,
            "move_back" => JournalInverseKind.MoveBack,
            "remove_created_entry" => JournalInverseKind.RemoveCreatedEntry,
            _ => throw new FormatException("Unknown persisted inverse kind."),
        };

    public static string ToStorage(RecoveryPrimitive primitive) =>
        primitive switch
        {
            RecoveryPrimitive.RenameBack => "rename_back",
            RecoveryPrimitive.MoveBack => "move_back",
            RecoveryPrimitive.RemoveCreatedEntry => "remove_created_entry",
            _ => throw new ArgumentOutOfRangeException(nameof(primitive)),
        };

    public static RecoveryPrimitive ParseRecoveryPrimitive(string value) =>
        value switch
        {
            "rename_back" => RecoveryPrimitive.RenameBack,
            "move_back" => RecoveryPrimitive.MoveBack,
            "remove_created_entry" => RecoveryPrimitive.RemoveCreatedEntry,
            _ => throw new FormatException("Unknown persisted recovery primitive."),
        };

    public static string ToStorage(SkillLifecycleState state) =>
        state switch
        {
            SkillLifecycleState.Draft => "draft",
            SkillLifecycleState.Approved => "approved",
            SkillLifecycleState.Practiced => "practiced",
            SkillLifecycleState.Reliable => "reliable",
            SkillLifecycleState.Delegated => "delegated",
            SkillLifecycleState.Stale => "stale",
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

    public static SkillLifecycleState ParseSkillLifecycle(string value) =>
        value switch
        {
            "draft" => SkillLifecycleState.Draft,
            "approved" => SkillLifecycleState.Approved,
            "practiced" => SkillLifecycleState.Practiced,
            "reliable" => SkillLifecycleState.Reliable,
            "delegated" => SkillLifecycleState.Delegated,
            "stale" => SkillLifecycleState.Stale,
            _ => throw new FormatException("Unknown persisted skill lifecycle."),
        };

    public static string ToStorage(TeachingEpisodeState state) =>
        state switch
        {
            TeachingEpisodeState.Started => "started",
            TeachingEpisodeState.BaselineCaptured => "baseline_captured",
            TeachingEpisodeState.ObservingEffects => "observing_effects",
            TeachingEpisodeState.Stopped => "stopped",
            TeachingEpisodeState.Reconciled => "reconciled",
            TeachingEpisodeState.Invalid => "invalid",
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

    public static string ToStorage(TeachingEvidenceState state) =>
        state switch
        {
            TeachingEvidenceState.Pending => "pending",
            TeachingEvidenceState.Complete => "complete",
            TeachingEvidenceState.Incomplete => "incomplete",
            TeachingEvidenceState.Ambiguous => "ambiguous",
            TeachingEvidenceState.Unsupported => "unsupported",
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

    public static string ToStorage(PlanApprovalPurpose purpose) =>
        purpose switch
        {
            PlanApprovalPurpose.Production => "production",
            PlanApprovalPurpose.Rehearsal => "rehearsal",
            PlanApprovalPurpose.Undo => "undo",
            _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
        };

    public static string ToStorage(ExecutionJournalKind kind) =>
        kind switch
        {
            ExecutionJournalKind.Standard => "standard",
            ExecutionJournalKind.Recovery => "recovery",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    public static ExecutionJournalKind ParseJournalKind(string value) =>
        value switch
        {
            "standard" => ExecutionJournalKind.Standard,
            "recovery" => ExecutionJournalKind.Recovery,
            _ => throw new FormatException("Unknown persisted journal kind."),
        };

    public static async Task BeginImmediateAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(connection, "BEGIN IMMEDIATE;", cancellationToken)
            .ConfigureAwait(false);

    public static async Task CommitAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(connection, "COMMIT;", cancellationToken)
            .ConfigureAwait(false);

    public static async Task RollbackAsync(SqliteConnection connection)
    {
        try
        {
            await ExecuteAsync(connection, "ROLLBACK;", CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // Preserve the original storage failure.
        }
    }

    public static string MapFailure(Exception exception) =>
        exception switch
        {
            PersistenceConflictException conflict => conflict.ReasonCode,
            FormatException or JsonException or ArgumentException =>
                "persistence.payload_invalid",
            InvalidOperationException => "persistence.not_ready",
            SqliteException sqlite when sqlite.SqliteErrorCode is 5 or 6 =>
                "persistence.writer_busy",
            SqliteException sqlite when sqlite.SqliteErrorCode == 19 =>
                "persistence.constraint_rejected",
            SqliteException => "persistence.sqlite_failure",
            IOException or UnauthorizedAccessException or System.Security.SecurityException =>
                "persistence.storage_unavailable",
            _ => "persistence.failure",
        };

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class PersistenceConflictException(string reasonCode) : Exception
{
    public string ReasonCode { get; } = reasonCode;
}
