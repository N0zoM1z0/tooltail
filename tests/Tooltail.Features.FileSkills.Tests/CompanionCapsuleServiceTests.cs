using Tooltail.Contracts.Capsules;
using Tooltail.Contracts.Json;
using Tooltail.Contracts.Skills;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Continuity;
using Tooltail.Features.FileSkills.Skills;

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
        Assert.Equal("capsule.import_ready_rebind_required", preview.ReasonCode);
        Assert.False(preview.CreatesAuthority);
        Assert.True(preview.CanImport);
        Assert.True(preview.SkillsRequireRebind);
        Assert.Equal(source.Companion.CompanionId, preview.Capsule!.Companion.CompanionId);
    }

    [Fact]
    public void PreparationImportsIdentityAndOnlyStaleUnboundSkillHistory()
    {
        CompanionCapsuleContract source = ReadExample();
        CompanionId emptyCompanion = new(
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));

        CapsuleImportPreparation prepared = CompanionCapsuleImportService.Prepare(
            ContractJson.Serialize(source),
            emptyCompanion);

        Assert.True(prepared.IsSuccess, prepared.ReasonCode);
        Assert.Equal("capsule.import_prepared", prepared.ReasonCode);
        Assert.False(prepared.Preview.CreatesAuthority);
        Assert.True(prepared.Preview.CanImport);
        Assert.Equal(source.Companion.CompanionId, prepared.State!.ImportedCompanion.Id.Value);
        Assert.Equal(emptyCompanion, prepared.State.ExpectedEmptyCompanionId);
        Assert.NotEmpty(prepared.State.SkillVersions);
        Assert.All(
            prepared.State.SkillVersions,
            version =>
            {
                Assert.Equal(SkillLifecycleState.Stale, version.Version.Lifecycle);
                Assert.Null(version.ApprovedUtc);
                Assert.Equal(
                    source.Companion.CompanionId,
                    version.CompanionId.Value);
            });
        Assert.Single(
            prepared.State.SkillVersions,
            static version => version.MakeCurrent);
    }

    [Fact]
    public void NonLinearSkillHistoryCannotReachImportPreparation()
    {
        CompanionCapsuleContract source = ReadExample();
        CapsuleSkillContract original = source.Skills[0];
        CompanionCapsuleContract skippedVersion = source with
        {
            Skills =
            [
                original with
                {
                    SkillSpec = original.SkillSpec with
                    {
                        Version = 2,
                        Provenance = original.SkillSpec.Provenance with
                        {
                            ParentVersion = null,
                        },
                    },
                },
            ],
        };

        CapsuleImportPreparation prepared = CompanionCapsuleImportService.Prepare(
            ContractJson.Serialize(skippedVersion),
            new CompanionId(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")));

        Assert.False(prepared.IsSuccess);
        Assert.Equal("capsule.skill_history_invalid", prepared.ReasonCode);
        Assert.Null(prepared.State);
        Assert.False(prepared.Preview.CreatesAuthority);
    }

    [Fact]
    public void ExplicitRebindCreatesParentLinkedDraftInputChangingOnlyScope()
    {
        SkillSpecContract parent = ReadExample().Skills[0].SkillSpec;
        GrantId newGrant = new(
            Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"));

        CapsuleSkillRebindResult rebound = CompanionCapsuleRebindService.Rebind(
            parent,
            newGrant,
            parent.CreatedAt.AddDays(1));

        Assert.True(rebound.IsSuccess, rebound.ReasonCode);
        Assert.Equal("capsule.rebind_ready", rebound.ReasonCode);
        Assert.Equal(parent.Version + 1, rebound.Rebound!.Version);
        Assert.Equal(parent.Version, rebound.Rebound.Provenance.ParentVersion);
        Assert.Equal(newGrant.Value, rebound.Rebound.Applicability.RootGrantId);
        Assert.Equal(parent.Applicability.Match, rebound.Rebound.Applicability.Match);
        Assert.Equal(parent.Steps, rebound.Rebound.Steps);
        Assert.NotEqual(
            CanonicalSkillSpec.ComputeHash(parent),
            rebound.SpecificationHash);
        Assert.Equal(["scope_binding"], rebound.SemanticDiff!.ChangedFields);
    }

    [Fact]
    public void RebindRejectsOldGrantAndNonIncreasingTime()
    {
        SkillSpecContract parent = ReadExample().Skills[0].SkillSpec;

        CapsuleSkillRebindResult sameGrant = CompanionCapsuleRebindService.Rebind(
            parent,
            new GrantId(parent.Applicability.RootGrantId),
            parent.CreatedAt.AddDays(1));
        CapsuleSkillRebindResult oldTime = CompanionCapsuleRebindService.Rebind(
            parent,
            new GrantId(Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd")),
            parent.CreatedAt);

        Assert.False(sameGrant.IsSuccess);
        Assert.Equal("capsule.rebind_grant_invalid", sameGrant.ReasonCode);
        Assert.False(oldTime.IsSuccess);
        Assert.Equal("capsule.rebind_time_invalid", oldTime.ReasonCode);
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
