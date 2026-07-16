using System.Security.Cryptography;
using System.Text;
using Tooltail.Contracts.Skills;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Compilation;
using Tooltail.Features.FileSkills.Correction;
using Tooltail.Features.FileSkills.Reconciliation;
using Tooltail.Features.FileSkills.Skills;
using Tooltail.Features.FileSkills.Snapshots;

namespace Tooltail.Features.FileSkills.Tests.Correction;

public sealed class SkillCorrectionServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 16, 8, 0, 0, TimeSpan.Zero);
    private static readonly GrantId Grant = new(
        Guid.Parse("bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb"));

    [Fact]
    public void NegativeExampleCreatesParentLinkedDraftWithChangedEdgeBehavior()
    {
        TeachingFileExample[] positives = MoveExamples();
        SkillSpecContract parent = CompileParent(positives);
        FolderSnapshotEntry edge = File("Inbox\\notes.pdf", "negative", "negative");
        FolderSnapshotEntry wrongOrigin = File(
            "Other\\invoice-9.pdf",
            "wrong-origin",
            "wrong-origin");

        SkillCorrectionResult result = SkillCorrectionService.Compile(
            new SkillCorrectionRequest(
                SkillCorrectionKind.NegativeExample,
                parent,
                Now.AddMinutes(1),
                positives,
                negativeExamples: [edge, wrongOrigin]));

        Assert.True(result.IsReady, result.ReasonCode);
        Assert.Equal(2, result.Specification!.Version);
        Assert.Equal(1, result.Specification.Provenance.ParentVersion);
        Assert.Equal(SkillLifecycleState.Draft, result.ProposedLifecycle);
        Assert.True(result.RequiresRehearsal);
        Assert.True(result.RequiresExactPlanApproval);
        Assert.True(result.SemanticDiff!.MatchChanged);
        Assert.False(SkillMatcher.Matches(result.Specification.Applicability.Match, edge));
        Assert.True(SkillMatcher.Matches(parent.Applicability.Match, edge));
        Assert.NotNull(result.SemanticDiffJson);
    }

    [Fact]
    public void ExplicitClarificationCanNarrowBothOriginAndFilenameScope()
    {
        TeachingFileExample[] positives = MoveExamples();
        SkillSpecContract parent = CompileParent(positives);

        SkillCorrectionResult result = SkillCorrectionService.Compile(
            new SkillCorrectionRequest(
                SkillCorrectionKind.ExplicitClarification,
                parent,
                Now.AddMinutes(1),
                positives,
                clarifications: NarrowAnswers()));

        Assert.True(result.IsReady, result.ReasonCode);
        Assert.Equal("Inbox", result.Specification!.Applicability.Match.OriginRelativeDirectory);
        Assert.Equal("invoice", result.Specification.Applicability.Match.Filename!.Contains);
        Assert.Equal(["match"], result.SemanticDiff!.ChangedFields);
    }

    [Fact]
    public void AdditionalPositiveThatDoesNotChangeExecutableSemanticsIsNotACorrection()
    {
        TeachingFileExample[] original = MoveExamples();
        SkillSpecContract parent = CompileParent(original);
        TeachingFileExample third = Example(
            3,
            "Inbox\\invoice-3.pdf",
            "Archive\\invoice-3.pdf",
            "three");

        SkillCorrectionResult result = SkillCorrectionService.Compile(
            new SkillCorrectionRequest(
                SkillCorrectionKind.PositiveExample,
                parent,
                Now.AddMinutes(1),
                [.. original, third],
                clarifications: BroadAnswers()));

        Assert.Equal(SkillCorrectionStatus.NoExecutableChange, result.Status);
        Assert.Equal("correction.no_executable_change", result.ReasonCode);
        Assert.Null(result.Specification);
        Assert.False(result.RequiresRehearsal);
    }

    [Fact]
    public void CorrectionRejectsEvidenceThatDropsParentExamples()
    {
        TeachingFileExample[] original = MoveExamples();
        SkillSpecContract parent = CompileParent(original);

        SkillCorrectionResult result = SkillCorrectionService.Compile(
            new SkillCorrectionRequest(
                SkillCorrectionKind.NegativeExample,
                parent,
                Now.AddMinutes(1),
                [original[0], Example(
                    3,
                    "Inbox\\invoice-3.pdf",
                    "Archive\\invoice-3.pdf",
                    "three")],
                negativeExamples: [File("Inbox\\notes.pdf", "negative", "negative")]));

        Assert.Equal(SkillCorrectionStatus.Invalid, result.Status);
        Assert.Equal("correction.evidence_lineage_invalid", result.ReasonCode);
    }

    private static SkillSpecContract CompileParent(
        IReadOnlyList<TeachingFileExample> examples)
    {
        SkillCompilationResult compilation = DeterministicSkillCompiler.Compile(
            new SkillCompilationRequest(
                new SkillId(Guid.Parse("aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa")),
                1,
                "Archive invoices",
                "Move matching invoice files into Archive without overwriting anything.",
                Grant,
                Now,
                examples,
                userAnswers: BroadAnswers()));
        Assert.Equal(SkillCompilationStatus.Ready, compilation.Status);
        return compilation.SelectedCandidate!.Specification;
    }

    private static TeachingFileExample[] MoveExamples() =>
    [
        Example(1, "Inbox\\invoice-1.pdf", "Archive\\invoice-1.pdf", "one"),
        Example(2, "Inbox\\invoice-2.pdf", "Archive\\invoice-2.pdf", "two"),
    ];

    private static SkillUserAnswerContract[] BroadAnswers() =>
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

    private static SkillUserAnswerContract[] NarrowAnswers() =>
    [
        new SkillUserAnswerContract
        {
            QuestionCode = "match.origin_scope",
            SelectedValue = "same_directory",
        },
        new SkillUserAnswerContract
        {
            QuestionCode = "match.filename_scope",
            SelectedValue = "contains_token",
        },
    ];

    private static TeachingFileExample Example(
        int number,
        string sourcePath,
        string destinationPath,
        string identity)
    {
        FolderSnapshotEntry source = File(sourcePath, sourcePath, identity);
        FolderSnapshotEntry destination = File(destinationPath, sourcePath, identity);
        return new TeachingFileExample(
            new ExampleId(Guid.Parse($"44444444-4444-4444-8444-{number:000000000000}")),
            new TeachingEpisodeId(Guid.Parse("33333333-3333-4333-8333-333333333333")),
            Grant,
            new ResourceRootIdentity("winfs-v1:volume:correction-root"),
            new ReconciledFileEffect(
                ReconciledEffectKind.Moved,
                sourcePath,
                destinationPath,
                source,
                destination,
                "reconcile.test_effect"));
    }

    private static FolderSnapshotEntry File(
        string path,
        string content,
        string identity)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        return new FolderSnapshotEntry(
            path,
            SnapshotEntryKind.File,
            bytes.Length,
            Now.AddDays(-1),
            Now,
            FileAttributes.Archive,
            isReparsePoint: false,
            "volume",
            identity,
            SnapshotContentHashStatus.Computed,
            new ContentHash(Convert.ToHexStringLower(SHA256.HashData(bytes))));
    }
}
