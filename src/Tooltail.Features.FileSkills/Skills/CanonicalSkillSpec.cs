using System.Security.Cryptography;
using Tooltail.Contracts.Json;
using Tooltail.Contracts.Skills;
using Tooltail.Domain.Execution;

namespace Tooltail.Features.FileSkills.Skills;

public static class CanonicalSkillSpec
{
    public static byte[] Encode(SkillSpecContract specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        SkillValidationResult validation = SkillSpecValidator.Validate(specification);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                "Only a semantically valid SkillSpec has a canonical encoding.",
                nameof(specification));
        }

        return ContractJson.Serialize(Normalize(specification));
    }

    public static SkillSpecificationHash ComputeHash(SkillSpecContract specification) =>
        new(Convert.ToHexStringLower(SHA256.HashData(Encode(specification))));

    private static SkillSpecContract Normalize(SkillSpecContract specification) =>
        specification with
        {
            Applicability = specification.Applicability with
            {
                Match = specification.Applicability.Match with
                {
                    Extensions = specification.Applicability.Match.Extensions?
                        .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(static value => value, StringComparer.Ordinal)
                        .ToArray(),
                },
            },
            Variables = specification.Variables
                .OrderBy(static variable => variable.Name, StringComparer.Ordinal)
                .ToArray(),
            Provenance = specification.Provenance with
            {
                TeachingEpisodeIds = specification.Provenance.TeachingEpisodeIds
                    .Order()
                    .ToArray(),
                ExampleIds = specification.Provenance.ExampleIds
                    .Order()
                    .ToArray(),
                UserAnswers = specification.Provenance.UserAnswers
                    .OrderBy(static answer => answer.QuestionCode, StringComparer.Ordinal)
                    .ToArray(),
            },
        };
}
