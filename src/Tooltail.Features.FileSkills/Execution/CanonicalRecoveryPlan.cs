using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Permissions;
using Tooltail.Features.FileSkills.Paths;

namespace Tooltail.Features.FileSkills.Execution;

public static class CanonicalRecoveryPlan
{
    public const string ContractVersion = "tooltail.recovery-plan/1";

    public static PathSafetyResult<RecoveryPlan> Create(RecoveryPlanDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        PathSafetyError? error = ValidatePaths(definition);
        return error is null
            ? PathSafetyResult.Success(
                new RecoveryPlan(definition, ComputeFingerprint(definition)))
            : PathSafetyResult.Failure<RecoveryPlan>(error.Code, error.Message);
    }

    public static PlanFingerprint ComputeFingerprint(RecoveryPlanDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new PlanFingerprint(
            Convert.ToHexStringLower(SHA256.HashData(Encode(definition))));
    }

    public static bool HasValidFingerprint(RecoveryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Fingerprint == ComputeFingerprint(plan.Definition);
    }

    public static PathSafetyError? ValidatePaths(RecoveryPlanDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        foreach (PlannedRecoveryOperation operation in definition.Operations)
        {
            PathSafetyResult<WindowsRelativePath> source =
                WindowsPathPolicy.ParseRelative(operation.SourceRelativePath);
            if (!source.IsSuccess)
            {
                return source.Error;
            }

            if (operation.DestinationRelativePath is null)
            {
                continue;
            }

            PathSafetyResult<WindowsRelativePath> destination =
                WindowsPathPolicy.ParseRelative(operation.DestinationRelativePath);
            if (!destination.IsSuccess)
            {
                return destination.Error;
            }

            PathSafetyResult<ValidatedPathPair> pair =
                WindowsPathPolicy.ValidateDistinctPair(source.Value!, destination.Value!);
            if (!pair.IsSuccess)
            {
                return pair.Error;
            }
        }

        return null;
    }

    public static byte[] Encode(RecoveryPlanDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
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
        writer.WriteString("contractVersion", ContractVersion);
        writer.WriteString("planId", FormatGuid(definition.Id.Value));
        writer.WritePropertyName("originalExecution");
        writer.WriteStartObject();
        writer.WriteString("executionId", FormatGuid(definition.OriginalExecutionId.Value));
        writer.WriteString("planId", FormatGuid(definition.OriginalPlanId.Value));
        writer.WriteString("planFingerprint", definition.OriginalPlanFingerprint.Value);
        writer.WriteEndObject();

        writer.WritePropertyName("skill");
        writer.WriteStartObject();
        writer.WriteString("id", FormatGuid(definition.SkillId.Value));
        writer.WriteNumber("version", definition.SkillVersion.Value);
        writer.WriteString("specificationSha256", definition.SkillSpecificationHash.Value);
        writer.WriteEndObject();

        writer.WritePropertyName("grant");
        writer.WriteStartObject();
        writer.WriteString("id", FormatGuid(definition.GrantId.Value));
        writer.WriteString("rootIdentity", definition.RootIdentity.Value);
        writer.WritePropertyName("actions");
        writer.WriteStartArray();
        foreach (string capability in definition.GrantedCapabilities
                     .Select(ToCanonicalCapability)
                     .Order(StringComparer.Ordinal))
        {
            writer.WriteStringValue(capability);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteString("createdUtc", FormatUtc(definition.CreatedUtc));
        writer.WriteString("expiresUtc", FormatUtc(definition.ExpiresUtc));
        writer.WritePropertyName("operations");
        writer.WriteStartArray();
        foreach (PlannedRecoveryOperation operation in definition.Operations)
        {
            WriteOperation(writer, operation);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteOperation(
        Utf8JsonWriter writer,
        PlannedRecoveryOperation operation)
    {
        writer.WriteStartObject();
        writer.WriteNumber("sequence", operation.Sequence);
        writer.WriteNumber("originalStepSequence", operation.OriginalStepSequence);
        writer.WriteString("originalPrimitive", ToCanonicalPrimitive(operation.OriginalPrimitive));
        writer.WriteString("recoveryPrimitive", ToCanonicalRecoveryPrimitive(operation.Primitive));
        writer.WriteString("sourceRelativePath", operation.SourceRelativePath);
        if (operation.DestinationRelativePath is null)
        {
            writer.WriteNull("destinationRelativePath");
        }
        else
        {
            writer.WriteString("destinationRelativePath", operation.DestinationRelativePath);
        }

        writer.WriteBoolean(
            "originalDestinationWasAbsent",
            operation.OriginalDestinationWasAbsent);
        writer.WritePropertyName("expectedSource");
        WriteEntryEvidence(writer, operation.ExpectedSource);
        writer.WriteEndObject();
    }

    private static void WriteEntryEvidence(
        Utf8JsonWriter writer,
        VerifiedEntryEvidence evidence)
    {
        writer.WriteStartObject();
        writer.WriteString(
            "kind",
            evidence.Kind == VerifiedEntryKind.File ? "file" : "directory");
        writer.WriteString("volumeIdentity", evidence.VolumeIdentity);
        writer.WriteString("entryIdentity", evidence.EntryIdentity);
        if (evidence.Length is null)
        {
            writer.WriteNull("length");
        }
        else
        {
            writer.WriteNumber("length", evidence.Length.Value);
        }

        writer.WriteString("creationUtc", FormatUtc(evidence.CreationUtc));
        writer.WriteString("lastWriteUtc", FormatUtc(evidence.LastWriteUtc));
        writer.WriteNumber("attributes", evidence.Attributes);
        if (evidence.ContentHash is null)
        {
            writer.WriteNull("contentSha256");
        }
        else
        {
            writer.WriteString("contentSha256", evidence.ContentHash.Value);
        }

        writer.WriteEndObject();
    }

    private static string ToCanonicalRecoveryPrimitive(RecoveryPrimitive primitive) =>
        primitive switch
        {
            RecoveryPrimitive.RenameBack => "rename_back",
            RecoveryPrimitive.MoveBack => "move_back",
            RecoveryPrimitive.RemoveCreatedEntry => "remove_created_entry",
            _ => throw new ArgumentOutOfRangeException(nameof(primitive)),
        };

    private static string ToCanonicalPrimitive(FilePrimitive primitive) =>
        primitive switch
        {
            FilePrimitive.EnsureDirectory => "ensure_directory",
            FilePrimitive.RenameFile => "rename_file",
            FilePrimitive.MoveFile => "move_file",
            FilePrimitive.CopyFile => "copy_file",
            _ => throw new ArgumentOutOfRangeException(nameof(primitive)),
        };

    private static string ToCanonicalCapability(GrantCapability capability) =>
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

    private static string FormatGuid(Guid value) => value.ToString("D");

    private static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
            CultureInfo.InvariantCulture);
}
