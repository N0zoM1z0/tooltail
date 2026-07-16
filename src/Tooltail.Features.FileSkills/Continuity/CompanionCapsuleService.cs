using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Tooltail.Contracts.Capsules;
using Tooltail.Contracts.Json;
using Tooltail.Contracts.Skills;
using Tooltail.Features.FileSkills.Skills;

namespace Tooltail.Features.FileSkills.Continuity;

public sealed record CapsuleValidationError(string Field, string Code);

public sealed record CapsuleValidationResult
{
    internal CapsuleValidationResult(IEnumerable<CapsuleValidationError> errors)
    {
        CapsuleValidationError[] materialized = errors.ToArray();
        Errors = new ReadOnlyCollection<CapsuleValidationError>(materialized);
    }

    public IReadOnlyList<CapsuleValidationError> Errors { get; }

    public bool IsValid => Errors.Count == 0;

    public string ReasonCode => IsValid
        ? "capsule.valid"
        : Errors[0].Code;
}

public sealed record CapsuleEncodingResult(
    bool IsSuccess,
    string ReasonCode,
    byte[]? Bytes,
    CompanionCapsuleContract? Capsule);

public sealed record CapsuleImportPreview(
    bool IsSuccess,
    string ReasonCode,
    CompanionCapsuleContract? Capsule,
    bool CreatesAuthority,
    bool CanImport,
    bool SkillsRequireRebind);

public static class CompanionCapsuleService
{
    private const int MaximumSkills = 500;
    private static readonly Regex ProducerVersionPattern = new(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex BodyStylePattern = new(
        "^[a-z][a-z0-9_-]{0,31}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex AccentPattern = new(
        "^#[0-9A-Fa-f]{6}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));

    public static CapsuleValidationResult Validate(CompanionCapsuleContract? capsule)
    {
        List<CapsuleValidationError> errors = [];
        if (capsule is null)
        {
            Add(errors, "$", "capsule.null");
            return new CapsuleValidationResult(errors);
        }

        if (!string.Equals(capsule.SchemaVersion, ContractVersions.V1, StringComparison.Ordinal))
        {
            Add(errors, "schemaVersion", "capsule.schema_unsupported");
        }

        if (capsule.CapsuleId == Guid.Empty)
        {
            Add(errors, "capsuleId", "capsule.id_empty");
        }

        if (!IsUtc(capsule.ExportedAt))
        {
            Add(errors, "exportedAt", "capsule.time_not_utc");
        }

        ValidateProducer(capsule.Producer, errors);
        ValidateCompanion(capsule.Companion, capsule.ExportedAt, errors);
        ValidateContentPolicy(capsule.ContentPolicy, errors);
        ValidateSkills(capsule.Skills, errors);
        return new CapsuleValidationResult(errors);
    }

    public static CapsuleEncodingResult Encode(CompanionCapsuleContract capsule)
    {
        ArgumentNullException.ThrowIfNull(capsule);
        CapsuleValidationResult validation = Validate(capsule);
        if (!validation.IsValid)
        {
            return new CapsuleEncodingResult(
                false,
                validation.ReasonCode,
                null,
                null);
        }

        CompanionCapsuleContract normalized = capsule with
        {
            Skills = capsule.Skills
                .OrderBy(static skill => skill.SkillSpec.SkillId)
                .ThenBy(static skill => skill.SkillSpec.Version)
                .Select(NormalizeSkill)
                .ToArray(),
        };
        byte[] bytes = ContractJson.Serialize(normalized);
        CapsuleImportPreview readBack = ParseForImport(bytes);
        return readBack.IsSuccess
            ? new CapsuleEncodingResult(true, "capsule.encoded", bytes, normalized)
            : new CapsuleEncodingResult(false, readBack.ReasonCode, null, null);
    }

    public static CapsuleImportPreview ParseForImport(ReadOnlySpan<byte> utf8Json)
    {
        ContractParseResult<CompanionCapsuleContract> parsed =
            ContractJson.ParseCompanionCapsule(utf8Json);
        if (!parsed.IsSuccess)
        {
            return DisabledPreview(parsed.Error!.Code, capsule: null);
        }

        CapsuleValidationResult validation = Validate(parsed.Value);
        return validation.IsValid
            ? new CapsuleImportPreview(
                true,
                "capsule.import_disabled_rebind_required",
                parsed.Value,
                CreatesAuthority: false,
                CanImport: false,
                SkillsRequireRebind: true)
            : DisabledPreview(validation.ReasonCode, parsed.Value);
    }

    private static CapsuleSkillContract NormalizeSkill(CapsuleSkillContract skill)
    {
        byte[] canonical = CanonicalSkillSpec.Encode(skill.SkillSpec);
        ContractParseResult<SkillSpecContract> parsed = ContractJson.ParseSkillSpec(canonical);
        if (!parsed.IsSuccess)
        {
            throw new InvalidOperationException("A validated SkillSpec failed canonical readback.");
        }

        return skill with { SkillSpec = parsed.Value! };
    }

    private static void ValidateProducer(
        CapsuleProducerContract? producer,
        ICollection<CapsuleValidationError> errors)
    {
        if (producer is null)
        {
            Add(errors, "producer", "capsule.producer_missing");
            return;
        }

        if (!string.Equals(producer.Name, "Tooltail", StringComparison.Ordinal))
        {
            Add(errors, "producer.name", "capsule.producer_invalid");
        }

        if (string.IsNullOrWhiteSpace(producer.Version) ||
            producer.Version.Length > 64 ||
            !ProducerVersionPattern.IsMatch(producer.Version))
        {
            Add(errors, "producer.version", "capsule.producer_version_invalid");
        }
    }

    private static void ValidateCompanion(
        CapsuleCompanionContract? companion,
        DateTimeOffset exportedAt,
        ICollection<CapsuleValidationError> errors)
    {
        if (companion is null)
        {
            Add(errors, "companion", "capsule.companion_missing");
            return;
        }

        if (companion.CompanionId == Guid.Empty)
        {
            Add(errors, "companion.companionId", "capsule.companion_id_empty");
        }

        if (!IsUtc(companion.CreatedAt) || companion.CreatedAt > exportedAt)
        {
            Add(errors, "companion.createdAt", "capsule.companion_time_invalid");
        }

        if (string.IsNullOrWhiteSpace(companion.DisplayName) ||
            companion.DisplayName.Length > 48)
        {
            Add(errors, "companion.displayName", "capsule.companion_name_invalid");
        }

        if (companion.Presentation is null ||
            string.IsNullOrWhiteSpace(companion.Presentation.BodyStyle) ||
            !BodyStylePattern.IsMatch(companion.Presentation.BodyStyle) ||
            string.IsNullOrWhiteSpace(companion.Presentation.Accent) ||
            !AccentPattern.IsMatch(companion.Presentation.Accent))
        {
            Add(errors, "companion.presentation", "capsule.presentation_invalid");
        }
    }

    private static void ValidateContentPolicy(
        CapsuleContentPolicyContract? policy,
        ICollection<CapsuleValidationError> errors)
    {
        if (policy is null ||
            policy.ContainsRawPaths ||
            policy.ContainsRawFileNames ||
            policy.ContainsFileContents ||
            policy.ContainsModelTranscripts ||
            policy.ContainsCredentials)
        {
            Add(errors, "contentPolicy", "capsule.content_policy_unsafe");
        }
    }

    private static void ValidateSkills(
        IReadOnlyList<CapsuleSkillContract>? skills,
        ICollection<CapsuleValidationError> errors)
    {
        if (skills is null)
        {
            Add(errors, "skills", "capsule.skills_missing");
            return;
        }

        if (skills.Count > MaximumSkills)
        {
            Add(errors, "skills", "capsule.skill_limit_exceeded");
            return;
        }

        HashSet<(Guid SkillId, int Version)> versions = [];
        for (int index = 0; index < skills.Count; index++)
        {
            CapsuleSkillContract? skill = skills[index];
            string field = $"skills[{index}]";
            if (skill?.SkillSpec is null)
            {
                Add(errors, field, "capsule.skill_missing");
                continue;
            }

            SkillValidationResult specification = SkillSpecValidator.Validate(skill.SkillSpec);
            if (!specification.IsValid)
            {
                Add(errors, $"{field}.skillSpec", "capsule.skill_invalid");
            }

            if (!versions.Add((skill.SkillSpec.SkillId, skill.SkillSpec.Version)))
            {
                Add(errors, field, "capsule.skill_version_duplicate");
            }

            if (!Enum.IsDefined(skill.ExportedLifecycleState))
            {
                Add(errors, $"{field}.exportedLifecycleState", "capsule.lifecycle_invalid");
            }

            if (skill.SourceGrantBinding is null ||
                skill.SourceGrantBinding.SourceGrantId == Guid.Empty ||
                skill.SourceGrantBinding.ImportBehavior !=
                    CapsuleImportBehavior.RequireUserRebind ||
                skill.SourceGrantBinding.SourceGrantId !=
                    skill.SkillSpec.Applicability.RootGrantId)
            {
                Add(errors, $"{field}.sourceGrantBinding", "capsule.binding_invalid");
            }

            ValidateEvidence(skill.EvidenceSummary, field, errors);
        }

        foreach (CapsuleSkillContract skill in skills.Where(static value => value is not null))
        {
            int? parent = skill.SkillSpec.Provenance.ParentVersion;
            if (parent is not null && !versions.Contains((skill.SkillSpec.SkillId, parent.Value)))
            {
                Add(errors, "skills", "capsule.parent_version_missing");
            }
        }
    }

    private static void ValidateEvidence(
        CapsuleEvidenceSummaryContract? evidence,
        string field,
        ICollection<CapsuleValidationError> errors)
    {
        if (evidence is null ||
            evidence.VerifiedSuccessCount < 0 ||
            evidence.VerifiedFailureCount < 0 ||
            evidence.CorrectionCount < 0 ||
            (evidence.LastVerifiedAt is not null && !IsUtc(evidence.LastVerifiedAt.Value)))
        {
            Add(errors, $"{field}.evidenceSummary", "capsule.evidence_invalid");
        }
    }

    private static CapsuleImportPreview DisabledPreview(
        string reasonCode,
        CompanionCapsuleContract? capsule) =>
        new(
            false,
            reasonCode,
            capsule,
            CreatesAuthority: false,
            CanImport: false,
            SkillsRequireRebind: true);

    private static bool IsUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero;

    private static void Add(
        ICollection<CapsuleValidationError> errors,
        string field,
        string code) => errors.Add(new CapsuleValidationError(field, code));
}
