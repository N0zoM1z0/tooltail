using System.Text;
using Tooltail.Application.Abstractions;
using Tooltail.Contracts.Skills;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Correction;
using Tooltail.Features.FileSkills.Presentation;
using Tooltail.Features.FileSkills.Skills;

namespace Tooltail.Desktop.Presentation;

public sealed record SkillCorrectionWorkflowResult(
    bool IsSuccess,
    string ReasonCode,
    SkillCorrectionResult? Correction,
    SkillCompilationWorkflowResult? CorrectedCompilation,
    bool CausalProbeChanged);

public sealed class SkillCorrectionWorkflowService
{
    private readonly IFileSkillStateStore stateStore;
    private readonly IClock clock;

    public SkillCorrectionWorkflowService(
        IFileSkillStateStore stateStore,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(clock);
        this.stateStore = stateStore;
        this.clock = clock;
    }

    public async Task<SkillCorrectionWorkflowResult> CreateExplicitClarificationAsync(
        SafeLabGrantResult lab,
        TeachingWorkflowResult teaching,
        SkillCompilationWorkflowResult parentCompilation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lab);
        ArgumentNullException.ThrowIfNull(teaching);
        ArgumentNullException.ThrowIfNull(parentCompilation);
        if (!lab.IsSuccess || lab.Grant is null ||
            !teaching.IsSuccess || teaching.Examples.Count < 2 ||
            !parentCompilation.IsSuccess ||
            parentCompilation.Specification is null ||
            parentCompilation.CardRequest is null)
        {
            return Failure("correction.desktop_request_invalid");
        }

        SkillSpecContract parent = parentCompilation.Specification;
        StateReadResult<SkillVersionStateRecord> storedParent =
            await stateStore.LoadSkillVersionAsync(
                new SkillId(parent.SkillId),
                new SkillVersionNumber(parent.Version),
                cancellationToken).ConfigureAwait(false);
        if (!storedParent.IsSuccess)
        {
            return Failure(storedParent.ReasonCode);
        }

        SkillUserAnswerContract[] broadenedAnswers =
        [
            new SkillUserAnswerContract
            {
                QuestionCode = "match.origin_scope",
                SelectedValue = "any_directory",
            },
            new SkillUserAnswerContract
            {
                QuestionCode = "match.filename_scope",
                SelectedValue = "any_filename",
            },
        ];
        SkillCorrectionResult correction = SkillCorrectionService.Compile(
            new SkillCorrectionRequest(
                SkillCorrectionKind.ExplicitClarification,
                parent,
                clock.UtcNow,
                teaching.Examples,
                clarifications: broadenedAnswers));
        if (!correction.IsReady ||
            correction.Specification is null ||
            correction.SpecificationHash is null ||
            correction.SemanticDiffJson is null)
        {
            return new SkillCorrectionWorkflowResult(
                false,
                correction.ReasonCode,
                correction,
                null,
                false);
        }

        bool causalProbeChanged = teaching.Examples
            .Select(static example => example.Effect.After)
            .Where(static entry => entry is not null)
            .Any(entry =>
                SkillMatcher.Matches(
                    parent.Applicability.Match,
                    entry!) !=
                SkillMatcher.Matches(
                    correction.Specification.Applicability.Match,
                    entry!));
        if (!causalProbeChanged)
        {
            return new SkillCorrectionWorkflowResult(
                false,
                "correction.causal_probe_unchanged",
                correction,
                null,
                false);
        }

        SkillSpecContract specification = correction.Specification;
        SkillVersion correctedVersion = new(
            new SkillId(specification.SkillId),
            new SkillVersionNumber(specification.Version),
            new SkillVersionNumber(parent.Version),
            correction.SpecificationHash.Value,
            specification.Compiler.Version,
            specification.Compatibility.MinimumExecutorVersion,
            SkillLifecycleState.Draft,
            specification.CreatedAt);
        StateWriteResult stored = await stateStore.StoreSkillVersionAsync(
            new SkillVersionStateRecord(
                storedParent.Value!.CompanionId,
                storedParent.Value.DisplayName,
                storedParent.Value.SkillCreatedUtc,
                correctedVersion,
                MakeCurrent: true,
                specification.SchemaVersion,
                Encoding.UTF8.GetString(CanonicalSkillSpec.Encode(specification)),
                "tooltail.deterministic-correction",
                ApprovedUtc: null,
                correction.SemanticDiffJson),
            cancellationToken).ConfigureAwait(false);
        if (!stored.IsSuccess)
        {
            return new SkillCorrectionWorkflowResult(
                false,
                stored.FailureCode!,
                correction,
                null,
                true);
        }

        SkillCardRequest cardRequest = new(
            specification,
            SkillLifecycleState.Draft,
            $"Tooltail safe lab — grant {lab.Grant.Id.Value:D}",
            lab.Grant.Capabilities,
            teaching.Examples.Take(5).Select(static example => new SkillCardSample(
                example.Effect.SourceRelativePath!,
                example.Effect.DestinationRelativePath!)),
            evidence: [],
            parentSpecification: parent);
        SkillCardViewModel card = SkillCardBuilder.Build(cardRequest);
        SkillCompilationWorkflowResult correctedCompilation = new(
            true,
            "correction.draft_persisted",
            correction.Compilation,
            card,
            specification,
            cardRequest);
        return new SkillCorrectionWorkflowResult(
            true,
            "correction.draft_persisted",
            correction,
            correctedCompilation,
            true);
    }

    private static SkillCorrectionWorkflowResult Failure(string reasonCode) =>
        new(false, reasonCode, null, null, false);
}
