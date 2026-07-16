using System.Text;
using Tooltail.Application.Abstractions;
using Tooltail.Contracts.Skills;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Compilation;
using Tooltail.Features.FileSkills.Presentation;
using Tooltail.Features.FileSkills.Skills;

namespace Tooltail.Desktop.Presentation;

public sealed record SkillCompilationWorkflowResult(
    bool IsSuccess,
    string ReasonCode,
    SkillCompilationResult? Compilation,
    SkillCardViewModel? Card,
    SkillSpecContract? Specification,
    SkillCardRequest? CardRequest);

public sealed class SkillCompilationWorkflowService
{
    private readonly IFileSkillStateStore stateStore;
    private readonly IClock clock;
    private readonly IIdGenerator idGenerator;
    private PendingCompilation? pending;

    public SkillCompilationWorkflowService(
        IFileSkillStateStore stateStore,
        IClock clock,
        IIdGenerator idGenerator)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(idGenerator);
        this.stateStore = stateStore;
        this.clock = clock;
        this.idGenerator = idGenerator;
    }

    public async Task<SkillCompilationWorkflowResult> CompileAsync(
        SafeLabGrantResult lab,
        TeachingWorkflowResult teaching,
        IReadOnlyList<SkillUserAnswerContract> answers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lab);
        ArgumentNullException.ThrowIfNull(teaching);
        ArgumentNullException.ThrowIfNull(answers);
        if (!lab.IsSuccess || lab.Grant is null || lab.Root is null ||
            !teaching.IsSuccess || teaching.Episode is null ||
            teaching.Examples.Count < 2)
        {
            return Failure("compiler.teaching_evidence_missing");
        }

        if (pending is null || pending.EpisodeId != teaching.Episode.Id)
        {
            pending = new PendingCompilation(
                teaching.Episode.Id,
                new SkillId(idGenerator.NewId()),
                clock.UtcNow);
        }

        SkillCompilationResult compilation = DeterministicSkillCompiler.Compile(
            new SkillCompilationRequest(
                pending.SkillId,
                version: 1,
                "File invoice PDFs",
                "Move demonstrated invoice PDFs into the demonstrated directory with the same safe filename transformation.",
                lab.Grant.Id,
                pending.CreatedUtc,
                teaching.Examples,
                exclusions: null,
                answers));
        if (compilation.Status != SkillCompilationStatus.Ready)
        {
            return new SkillCompilationWorkflowResult(
                false,
                compilation.ReasonCode,
                compilation,
                null,
                null,
                null);
        }

        SkillSpecContract specification = compilation.SelectedCandidate!.Specification;
        SkillSpecificationHash hash = CanonicalSkillSpec.ComputeHash(specification);
        SkillVersion version = new(
            pending.SkillId,
            new SkillVersionNumber(1),
            parent: null,
            hash.Value,
            specification.Compiler.Version,
            specification.Compatibility.MinimumExecutorVersion,
            SkillLifecycleState.Draft,
            specification.CreatedAt);
        StateWriteResult stored = await stateStore.StoreSkillVersionAsync(
            new SkillVersionStateRecord(
                lab.Grant.CompanionId,
                specification.Name,
                specification.CreatedAt,
                version,
                MakeCurrent: true,
                specification.SchemaVersion,
                Encoding.UTF8.GetString(CanonicalSkillSpec.Encode(specification)),
                "tooltail.deterministic-template",
                ApprovedUtc: null,
                SemanticDiffJson: null),
            cancellationToken).ConfigureAwait(false);
        if (!stored.IsSuccess)
        {
            return new SkillCompilationWorkflowResult(
                false,
                stored.FailureCode!,
                compilation,
                null,
                null,
                null);
        }

        SkillCardRequest cardRequest = new(
            specification,
            SkillLifecycleState.Draft,
            $"Tooltail safe lab — grant {lab.Grant.Id.Value:D}",
            lab.Grant.Capabilities,
            teaching.Examples.Take(5).Select(static example => new SkillCardSample(
                example.Effect.SourceRelativePath!,
                example.Effect.DestinationRelativePath!)),
            [
                new SkillCardEvidence(
                    SkillCardEvidenceKind.TeachingComplete,
                    "teaching.evidence_complete",
                    teaching.Episode.StoppedAt ?? specification.CreatedAt,
                    hash),
            ]);
        SkillCardViewModel card = SkillCardBuilder.Build(cardRequest);
        return new SkillCompilationWorkflowResult(
            true,
            "compiler.draft_persisted",
            compilation,
            card,
            specification,
            cardRequest);
    }

    private static SkillCompilationWorkflowResult Failure(string reasonCode)
        => new(false, reasonCode, null, null, null, null);

    private sealed record PendingCompilation(
        TeachingEpisodeId EpisodeId,
        SkillId SkillId,
        DateTimeOffset CreatedUtc);
}
