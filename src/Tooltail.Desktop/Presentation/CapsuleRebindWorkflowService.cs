using Tooltail.Domain.Identifiers;
using Tooltail.Features.FileSkills.Continuity;
using Tooltail.Features.FileSkills.Presentation;

namespace Tooltail.Desktop.Presentation;

public sealed record CapsuleRebindWorkflowResult(
    bool IsSuccess,
    string ReasonCode,
    SkillCompilationWorkflowResult? Compilation,
    SkillId? ReboundSkillId,
    int RemainingStaleSkillCount);

public sealed class CapsuleRebindWorkflowService
{
    private readonly CompanionCapsuleRebindPersistenceService persistence;

    public CapsuleRebindWorkflowService(
        CompanionCapsuleRebindPersistenceService persistence)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        this.persistence = persistence;
    }

    public async Task<CapsuleRebindWorkflowResult> RebindNextAsync(
        CompanionId companionId,
        SafeLabGrantResult lab,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lab);
        if (companionId.Value == Guid.Empty ||
            !lab.IsSuccess || lab.Grant is null || lab.Root is null ||
            lab.Grant.CompanionId != companionId)
        {
            return Failure("capsule.rebind_grant_invalid");
        }

        CapsulePersistedRebindResult rebound =
            await persistence.RebindNextAsync(
                companionId,
                lab.Grant,
                cancellationToken).ConfigureAwait(false);
        if (!rebound.IsSuccess)
        {
            return Failure(rebound.ReasonCode);
        }

        SkillCardRequest cardRequest = new(
            rebound.Rebound!,
            Tooltail.Domain.Skills.SkillLifecycleState.Draft,
            $"New explicit grant {lab.Grant.Id.Value:D}",
            lab.Grant.Capabilities,
            samples: [],
            evidence: [],
            parentSpecification: rebound.Parent);
        SkillCompilationWorkflowResult compilation = new(
            true,
            "capsule.rebind_draft_persisted",
            Compilation: null,
            SkillCardBuilder.Build(cardRequest),
            rebound.Rebound,
            cardRequest);
        return new CapsuleRebindWorkflowResult(
            true,
            "capsule.rebind_draft_persisted",
            compilation,
            rebound.ReboundSkillId,
            rebound.RemainingStaleSkillCount);
    }

    private static CapsuleRebindWorkflowResult Failure(string reasonCode) =>
        new(false, reasonCode, null, null, 0);
}
