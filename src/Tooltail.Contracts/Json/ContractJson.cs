using System.Text.Json;
using System.Text.Json.Serialization;
using Tooltail.Contracts.AgentEvents;
using Tooltail.Contracts.Capsules;
using Tooltail.Contracts.Scopes;
using Tooltail.Contracts.Skills;

namespace Tooltail.Contracts.Json;

public static class ContractJson
{
    public const int AgentEventMaximumBytes = 64 * 1024;
    public const int WindowLeaseMaximumBytes = 64 * 1024;
    public const int SkillSpecMaximumBytes = 1024 * 1024;
    public const int CompanionCapsuleMaximumBytes = 16 * 1024 * 1024;

    public static JsonSerializerOptions SerializerOptions { get; } = CreateOptions();

    public static ContractParseResult<AgentEventContract> ParseAgentEvent(ReadOnlySpan<byte> utf8Json) =>
        ParseVersioned<AgentEventContract>(utf8Json, AgentEventMaximumBytes);

    public static ContractParseResult<WindowLeaseContract> ParseWindowLease(ReadOnlySpan<byte> utf8Json) =>
        ParseVersioned<WindowLeaseContract>(utf8Json, WindowLeaseMaximumBytes);

    public static ContractParseResult<SkillSpecContract> ParseSkillSpec(ReadOnlySpan<byte> utf8Json) =>
        ParseVersioned<SkillSpecContract>(utf8Json, SkillSpecMaximumBytes);

    public static ContractParseResult<CompanionCapsuleContract> ParseCompanionCapsule(ReadOnlySpan<byte> utf8Json) =>
        ParseVersioned<CompanionCapsuleContract>(utf8Json, CompanionCapsuleMaximumBytes);

    public static byte[] Serialize<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
    }

    private static ContractParseResult<T> ParseVersioned<T>(
        ReadOnlySpan<byte> utf8Json,
        int maximumBytes)
        where T : class, IVersionedContract
    {
        if (utf8Json.IsEmpty)
        {
            return ContractParseResult.Failure<T>(
                "contract.empty",
                "The contract payload is empty.");
        }

        if (utf8Json.Length > maximumBytes)
        {
            return ContractParseResult.Failure<T>(
                "contract.too_large",
                "The contract payload exceeds its bounded size limit.");
        }

        try
        {
            T? contract = JsonSerializer.Deserialize<T>(utf8Json, SerializerOptions);
            if (contract is null)
            {
                return ContractParseResult.Failure<T>(
                    "contract.null",
                    "The contract payload must be a JSON object.");
            }

            return contract.SchemaVersion == ContractVersions.V1
                ? ContractParseResult.Success(contract)
                : ContractParseResult.Failure<T>(
                    "contract.unsupported_schema_version",
                    "The contract schema version is not supported.");
        }
        catch (JsonException)
        {
            return ContractParseResult.Failure<T>(
                "contract.invalid_json",
                "The contract payload is malformed or violates its closed JSON shape.");
        }
        catch (NotSupportedException)
        {
            return ContractParseResult.Failure<T>(
                "contract.unsupported_shape",
                "The contract payload contains an unsupported discriminator or value.");
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            AllowTrailingCommas = false,
            AllowOutOfOrderMetadataProperties = true,
            MaxDepth = 64,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false,
        };
        options.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
    }
}

public static class ContractVersions
{
    public const string V1 = "1.0";
}

public interface IVersionedContract
{
    string SchemaVersion { get; }
}
