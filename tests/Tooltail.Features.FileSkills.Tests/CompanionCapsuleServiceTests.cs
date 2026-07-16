using Tooltail.Contracts.Capsules;
using Tooltail.Contracts.Json;
using Tooltail.Features.FileSkills.Continuity;

namespace Tooltail.Features.FileSkills.Tests;

public sealed class CompanionCapsuleServiceTests
{
    [Fact]
    public void EncodeValidatesCanonicalizesAndReadsBackWithoutAuthority()
    {
        CompanionCapsuleContract source = ReadExample();

        CapsuleEncodingResult encoded = CompanionCapsuleService.Encode(source);
        CapsuleImportPreview preview = CompanionCapsuleService.ParseForImport(
            encoded.Bytes!);

        Assert.True(encoded.IsSuccess, encoded.ReasonCode);
        Assert.Equal("capsule.encoded", encoded.ReasonCode);
        Assert.True(preview.IsSuccess, preview.ReasonCode);
        Assert.Equal("capsule.import_disabled_rebind_required", preview.ReasonCode);
        Assert.False(preview.CreatesAuthority);
        Assert.False(preview.CanImport);
        Assert.True(preview.SkillsRequireRebind);
        Assert.Equal(source.Companion.CompanionId, preview.Capsule!.Companion.CompanionId);
    }

    [Fact]
    public void RejectsContentPolicyThatClaimsSensitiveMaterial()
    {
        CompanionCapsuleContract source = ReadExample();
        CompanionCapsuleContract unsafeCapsule = source with
        {
            ContentPolicy = source.ContentPolicy with { ContainsCredentials = true },
        };

        CapsuleEncodingResult encoded = CompanionCapsuleService.Encode(unsafeCapsule);

        Assert.False(encoded.IsSuccess);
        Assert.Equal("capsule.content_policy_unsafe", encoded.ReasonCode);
        Assert.Null(encoded.Bytes);
    }

    [Fact]
    public void RejectsGrantBindingThatDoesNotMatchSkillSpecification()
    {
        CompanionCapsuleContract source = ReadExample();
        CapsuleSkillContract skill = source.Skills[0];
        CompanionCapsuleContract mismatched = source with
        {
            Skills =
            [
                skill with
                {
                    SourceGrantBinding = skill.SourceGrantBinding with
                    {
                        SourceGrantId = Guid.Parse(
                            "99999999-9999-4999-8999-999999999999"),
                    },
                },
            ],
        };

        CapsuleValidationResult validation = CompanionCapsuleService.Validate(mismatched);

        Assert.False(validation.IsValid);
        Assert.Contains(
            validation.Errors,
            static error => error.Code == "capsule.binding_invalid");
    }

    [Fact]
    public void RejectsCorrectionWhenParentVersionIsNotIncluded()
    {
        CompanionCapsuleContract source = ReadExample();
        CapsuleSkillContract skill = source.Skills[0];
        CompanionCapsuleContract missingParent = source with
        {
            Skills =
            [
                skill with
                {
                    SkillSpec = skill.SkillSpec with
                    {
                        Version = 2,
                        Provenance = skill.SkillSpec.Provenance with
                        {
                            ParentVersion = 1,
                        },
                    },
                },
            ],
        };

        CapsuleValidationResult validation = CompanionCapsuleService.Validate(missingParent);

        Assert.False(validation.IsValid);
        Assert.Contains(
            validation.Errors,
            static error => error.Code == "capsule.parent_version_missing");
    }

    [Fact]
    public void MalformedDocumentNeverProducesAnImportablePreview()
    {
        CapsuleImportPreview preview = CompanionCapsuleService.ParseForImport(
            "{}"u8);

        Assert.False(preview.IsSuccess);
        Assert.False(preview.CreatesAuthority);
        Assert.False(preview.CanImport);
        Assert.True(preview.SkillsRequireRebind);
    }

    private static CompanionCapsuleContract ReadExample()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "examples",
            "companion-capsule.example.json");
        ContractParseResult<CompanionCapsuleContract> parsed =
            ContractJson.ParseCompanionCapsule(File.ReadAllBytes(path));
        Assert.True(parsed.IsSuccess, parsed.Error?.Code);
        return parsed.Value!;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Tooltail.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName ??
            throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
