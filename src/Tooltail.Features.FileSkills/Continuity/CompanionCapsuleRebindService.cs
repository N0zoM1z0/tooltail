using System.Text;
using Tooltail.Contracts.Json;
using Tooltail.Contracts.Skills;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Features.FileSkills.Correction;
using Tooltail.Features.FileSkills.Skills;

namespace Tooltail.Features.FileSkills.Continuity;

public sealed record CapsuleSkillRebindResult(
    bool IsSuccess,
    string ReasonCode,
    SkillSpecContract? Parent,
    SkillSpecContract? Rebound,
    SkillSpecificationHash? SpecificationHash,
    SkillSemanticDiff? SemanticDiff,
    string? SemanticDiffJson);

public static class CompanionCapsuleRebindService
{
    public static CapsuleSkillRebindResult Rebind(
        SkillSpecContract parent,
        GrantId newGrantId,
        DateTimeOffset createdUtc)
    {
        ArgumentNullException.ThrowIfNull(parent);
        SkillValidationResult parentValidation = SkillSpecValidator.Validate(parent);
        if (!parentValidation.IsValid)
        {
            return Failure("capsule.rebind_parent_invalid", parent);
        }

        if (newGrantId.Value == Guid.Empty ||
            parent.Applicability.RootGrantId == newGrantId.Value)
        {
            return Failure("capsule.rebind_grant_invalid", parent);
        }

        if (createdUtc.Offset != TimeSpan.Zero || createdUtc <= parent.CreatedAt ||
            parent.Version == int.MaxValue)
        {
            return Failure("capsule.rebind_time_invalid", parent);
        }

        SkillSpecContract rebound = parent with
        {
            Version = checked(parent.Version + 1),
            CreatedAt = createdUtc,
            Applicability = parent.Applicability with
            {
                RootGrantId = newGrantId.Value,
            },
            Provenance = parent.Provenance with
            {
                ParentVersion = parent.Version,
            },
        };
        SkillValidationResult validation = SkillSpecValidator.Validate(rebound);
        if (!validation.IsValid)
        {
            return Failure("capsule.rebind_skill_invalid", parent);
        }

        SkillSemanticDiff diff = SkillCorrectionService.Compare(parent, rebound);
        if (!diff.ScopeBindingChanged ||
            diff.MatchChanged ||
            diff.TransformationChanged ||
            diff.PolicyChanged ||
            diff.VerificationChanged ||
            diff.ChangedFields.Count != 1 ||
            diff.ChangedFields[0] != "scope_binding")
        {
            return Failure("capsule.rebind_diff_invalid", parent);
        }

        return new CapsuleSkillRebindResult(
            true,
            "capsule.rebind_ready",
            parent,
            rebound,
            CanonicalSkillSpec.ComputeHash(rebound),
            diff,
            Encoding.UTF8.GetString(ContractJson.Serialize(diff)));
    }

    private static CapsuleSkillRebindResult Failure(
        string reasonCode,
        SkillSpecContract parent) =>
        new(false, reasonCode, parent, null, null, null, null);
}
