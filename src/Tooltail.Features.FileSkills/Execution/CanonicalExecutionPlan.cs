using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Permissions;
using Tooltail.Features.FileSkills.Paths;

namespace Tooltail.Features.FileSkills.Execution;

public static class CanonicalExecutionPlan
{
    public const string ContractVersion = "tooltail.execution-plan/1";

    public static PathSafetyResult<ExecutionPlan> Create(ExecutionPlanDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        PathSafetyError? error = ValidatePaths(definition);
        return error is null
            ? PathSafetyResult.Success(
                new ExecutionPlan(definition, ComputeFingerprint(definition)))
            : PathSafetyResult.Failure<ExecutionPlan>(error.Code, error.Message);
    }

    public static PlanFingerprint ComputeFingerprint(ExecutionPlanDefinition definition)
    {
        byte[] canonicalBytes = Encode(definition);
        byte[] digest = SHA256.HashData(canonicalBytes);
        return new PlanFingerprint(Convert.ToHexStringLower(digest));
    }

    public static bool HasValidFingerprint(ExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Fingerprint == ComputeFingerprint(plan.Definition);
    }

    public static PathSafetyError? ValidatePaths(ExecutionPlanDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        foreach (PlannedFileOperation operation in definition.Operations)
        {
            PathSafetyResult<WindowsRelativePath> destination =
                WindowsPathPolicy.ParseRelative(operation.DestinationRelativePath);
            if (!destination.IsSuccess)
            {
                return destination.Error;
            }

            if (operation.SourceRelativePath is null)
            {
                continue;
            }

            PathSafetyResult<WindowsRelativePath> source =
                WindowsPathPolicy.ParseRelative(operation.SourceRelativePath);
            if (!source.IsSuccess)
            {
                return source.Error;
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

    public static byte[] Encode(ExecutionPlanDefinition definition)
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
        foreach (PlannedFileOperation operation in definition.Operations)
        {
            WriteOperation(writer, operation);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteOperation(Utf8JsonWriter writer, PlannedFileOperation operation)
    {
        writer.WriteStartObject();
        writer.WriteNumber("sequence", operation.Sequence);
        writer.WriteString("primitive", ToCanonicalPrimitive(operation.Primitive));
        if (operation.SourceRelativePath is null)
        {
            writer.WriteNull("sourceRelativePath");
        }
        else
        {
            writer.WriteString("sourceRelativePath", operation.SourceRelativePath);
        }

        writer.WriteString("destinationRelativePath", operation.DestinationRelativePath);
        writer.WriteString(
            "destinationPrecondition",
            ToCanonicalDestinationPrecondition(operation.DestinationPrecondition));
        writer.WritePropertyName("sourceFingerprint");
        if (operation.SourceFingerprint is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            WriteSourceFingerprint(writer, operation.SourceFingerprint);
        }

        writer.WriteString("expectedSourceState", ToCanonicalSourceState(operation.ExpectedSourceState));
        writer.WriteString(
            "expectedDestinationState",
            ToCanonicalDestinationState(operation.ExpectedDestinationState));
        writer.WriteEndObject();
    }

    private static void WriteSourceFingerprint(
        Utf8JsonWriter writer,
        SourceFileFingerprint fingerprint)
    {
        writer.WriteStartObject();
        writer.WriteString("entryIdentity", fingerprint.EntryIdentity);
        writer.WriteNumber("length", fingerprint.Length);
        writer.WriteString("lastWriteUtc", FormatUtc(fingerprint.LastWriteUtc));
        if (fingerprint.ContentHash is null)
        {
            writer.WriteNull("contentSha256");
        }
        else
        {
            writer.WriteString("contentSha256", fingerprint.ContentHash.Value);
        }

        writer.WriteEndObject();
    }

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

    private static string ToCanonicalPrimitive(FilePrimitive primitive) =>
        primitive switch
        {
            FilePrimitive.EnsureDirectory => "ensure_directory",
            FilePrimitive.RenameFile => "rename_file",
            FilePrimitive.MoveFile => "move_file",
            FilePrimitive.CopyFile => "copy_file",
            _ => throw new ArgumentOutOfRangeException(nameof(primitive)),
        };

    private static string ToCanonicalDestinationPrecondition(DestinationPrecondition precondition) =>
        precondition switch
        {
            DestinationPrecondition.Absent => "absent",
            DestinationPrecondition.ExistingDirectory => "existing_directory",
            _ => throw new ArgumentOutOfRangeException(nameof(precondition)),
        };

    private static string ToCanonicalSourceState(ExpectedSourceState state) =>
        state switch
        {
            ExpectedSourceState.NotApplicable => "not_applicable",
            ExpectedSourceState.Absent => "absent",
            ExpectedSourceState.Unchanged => "unchanged",
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

    private static string ToCanonicalDestinationState(ExpectedDestinationState state) =>
        state switch
        {
            ExpectedDestinationState.DirectoryPresent => "directory_present",
            ExpectedDestinationState.FileMatchesSource => "file_matches_source",
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

    private static string FormatGuid(Guid value) => value.ToString("D");

    private static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}
