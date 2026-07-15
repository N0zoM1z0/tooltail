using System.Security.Cryptography;
using System.Text;
using Tooltail.Contracts.Skills;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Features.FileSkills.Compilation;
using Tooltail.Features.FileSkills.Reconciliation;
using Tooltail.Features.FileSkills.Skills;
using Tooltail.Features.FileSkills.Snapshots;

namespace Tooltail.Features.FileSkills.Tests.Compilation;

public sealed class DeterministicSkillCompilerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 16, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MaterialMatchDifferencesBecomeAtMostTwoTypedQuestions()
    {
        SkillCompilationRequest request = Request(MoveExamples());

        SkillCompilationResult result = DeterministicSkillCompiler.Compile(request);

        Assert.Equal(SkillCompilationStatus.NeedsClarification, result.Status);
        Assert.Equal(4, result.Candidates.Count);
        Assert.Collection(
            result.Questions,
            question => Assert.Equal("match.origin_scope", question.Code),
            question => Assert.Equal("match.filename_scope", question.Code));
        Assert.All(result.Candidates, candidate =>
            Assert.True(SkillSpecValidator.Validate(candidate.Specification).IsValid));
    }

    [Fact]
    public void ExplicitAnswersSelectOneValidCandidateWithExactProvenance()
    {
        SkillUserAnswerContract[] answers =
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
        SkillCompilationRequest request = Request(MoveExamples(), answers: answers);

        SkillCompilationResult result = DeterministicSkillCompiler.Compile(request);

        Assert.Equal(SkillCompilationStatus.Ready, result.Status);
        SkillSpecContract specification = result.SelectedCandidate!.Specification;
        Assert.Equal("Inbox", specification.Applicability.Match.OriginRelativeDirectory);
        Assert.Equal("invoice", specification.Applicability.Match.Filename?.Contains);
        Assert.Equal([".pdf"], specification.Applicability.Match.Extensions);
        Assert.IsType<EnsureDirectoryStepContract>(specification.Steps[0]);
        MoveFileStepContract move = Assert.IsType<MoveFileStepContract>(specification.Steps[1]);
        Assert.Equal("Archive", move.DestinationDirectoryTemplate);
        Assert.Equal("{{originalStem}}{{originalExtension}}", move.DestinationFileNameTemplate);
        Assert.Equal(2, specification.Provenance.ExampleIds.Count);
        Assert.Equal(
            answers.OrderBy(static answer => answer.QuestionCode, StringComparer.Ordinal),
            specification.Provenance.UserAnswers);
    }

    [Fact]
    public void ExampleOrderingCannotChangeCandidateOrCanonicalHash()
    {
        TeachingFileExample[] examples = MoveExamples();
        SkillUserAnswerContract[] answers = ScopeAnswers();
        SkillCompilationResult first = DeterministicSkillCompiler.Compile(
            Request(examples, answers: answers));
        SkillCompilationResult second = DeterministicSkillCompiler.Compile(
            Request(examples.Reverse(), answers: answers));

        Assert.Equal(first.SelectedCandidate!.Key, second.SelectedCandidate!.Key);
        Assert.Equal(
            CanonicalSkillSpec.ComputeHash(first.SelectedCandidate.Specification),
            CanonicalSkillSpec.ComputeHash(second.SelectedCandidate.Specification));
        Assert.Equal(
            "1c93dc4bc2f7996c73f0574c8c15081d685fe0966b3674b7f652ed1c0f025484",
            CanonicalSkillSpec.ComputeHash(first.SelectedCandidate.Specification).Value);
    }

    [Fact]
    public void NegativeExamplesEliminateEveryOverbroadCandidate()
    {
        FolderSnapshotEntry wrongName = File("Inbox\\notes.pdf", "notes", "negative-name");
        FolderSnapshotEntry wrongOrigin = File("Other\\invoice-9.pdf", "invoice", "negative-origin");
        SkillCompilationRequest request = Request(
            MoveExamples(),
            exclusions: [wrongName, wrongOrigin]);

        SkillCompilationResult result = DeterministicSkillCompiler.Compile(request);

        Assert.Equal(SkillCompilationStatus.Ready, result.Status);
        Assert.Equal(
            "origin=1;token=1;transform=preserve",
            result.SelectedCandidate!.Key);
        Assert.Equal(3, result.RejectedCauses.Count);
        Assert.All(result.RejectedCauses, cause =>
            Assert.Equal("compiler.matches_exclusion", cause.Code));
    }

    [Fact]
    public void ConstantAffixRenameCompilesToExplainableTemplate()
    {
        TeachingFileExample[] examples =
        [
            Example(
                1,
                ReconciledEffectKind.Renamed,
                "Inbox\\report-one.txt",
                "Inbox\\filed-report-one-final.txt",
                "one"),
            Example(
                2,
                ReconciledEffectKind.Renamed,
                "Inbox\\report-two.txt",
                "Inbox\\filed-report-two-final.txt",
                "two"),
        ];
        SkillCompilationResult result = DeterministicSkillCompiler.Compile(
            Request(examples, answers: ScopeAnswers()));

        Assert.Equal(SkillCompilationStatus.Ready, result.Status);
        RenameFileStepContract rename = Assert.IsType<RenameFileStepContract>(
            Assert.Single(result.SelectedCandidate!.Specification.Steps));
        Assert.Equal(
            "filed-{{originalStem}}-final{{originalExtension}}",
            rename.DestinationFileNameTemplate);
    }

    [Fact]
    public void MixedPrimitivesAndSingleExampleNeverProduceExecutableCandidate()
    {
        TeachingFileExample move = MoveExamples()[0];
        TeachingFileExample copy = Example(
            3,
            ReconciledEffectKind.Copied,
            "Inbox\\invoice-3.pdf",
            "Archive\\invoice-3.pdf",
            "three");

        SkillCompilationResult mixed = DeterministicSkillCompiler.Compile(
            Request([move, copy]));
        SkillCompilationResult single = DeterministicSkillCompiler.Compile(
            Request([move]));

        Assert.Equal(SkillCompilationStatus.UnsupportedEvidence, mixed.Status);
        Assert.Equal(SkillCompilationStatus.NeedsMoreExamples, single.Status);
        Assert.Empty(mixed.Candidates);
        Assert.Empty(single.Candidates);
    }

    [Fact]
    public void UnknownAnswersDuplicateEvidenceAndCrossVolumeEvidenceAreInvalid()
    {
        SkillCompilationResult unknownAnswer = DeterministicSkillCompiler.Compile(
            Request(
                MoveExamples(),
                answers:
                [
                    new SkillUserAnswerContract
                    {
                        QuestionCode = "unknown.question",
                        SelectedValue = "anything",
                    },
                ]));
        TeachingFileExample duplicate = Example(
            3,
            ReconciledEffectKind.Moved,
            "Inbox\\invoice-1.pdf",
            "Archive\\invoice-1.pdf",
            "duplicate");
        SkillCompilationResult duplicateEvidence = DeterministicSkillCompiler.Compile(
            Request([MoveExamples()[0], duplicate]));
        TeachingFileExample crossVolume = Example(
            3,
            ReconciledEffectKind.Moved,
            "Inbox\\invoice-3.pdf",
            "Archive\\invoice-3.pdf",
            "cross-volume",
            sourceVolume: "volume-a",
            destinationVolume: "volume-b");
        SkillCompilationResult scopeMismatch = DeterministicSkillCompiler.Compile(
            Request([MoveExamples()[0], crossVolume]));

        Assert.Equal(SkillCompilationStatus.InvalidRequest, unknownAnswer.Status);
        Assert.Equal(SkillCompilationStatus.InvalidRequest, duplicateEvidence.Status);
        Assert.Equal(SkillCompilationStatus.InvalidRequest, scopeMismatch.Status);
        Assert.Equal("compiler.answers_invalid", unknownAnswer.ReasonCode);
        Assert.Equal("compiler.examples_invalid", duplicateEvidence.ReasonCode);
        Assert.Equal("compiler.evidence_scope_mismatch", scopeMismatch.ReasonCode);
    }

    [Fact]
    public void CaseOnlyObservedRenameCannotBecomeAnExecutableCandidate()
    {
        TeachingFileExample[] examples =
        [
            Example(
                1,
                ReconciledEffectKind.Renamed,
                "Inbox\\REPORT-ONE.PDF",
                "Inbox\\report-one.pdf",
                "one"),
            Example(
                2,
                ReconciledEffectKind.Renamed,
                "Inbox\\REPORT-TWO.PDF",
                "Inbox\\report-two.pdf",
                "two"),
        ];

        SkillCompilationResult result = DeterministicSkillCompiler.Compile(
            Request(examples));

        Assert.Equal(SkillCompilationStatus.NoCandidate, result.Status);
        Assert.Empty(result.Candidates);
        Assert.All(
            result.RejectedCauses,
            static cause => Assert.Equal("compiler.positive_not_covered", cause.Code));
    }

    private static SkillCompilationRequest Request(
        IEnumerable<TeachingFileExample> examples,
        IEnumerable<FolderSnapshotEntry>? exclusions = null,
        IEnumerable<SkillUserAnswerContract>? answers = null) =>
        new(
            new SkillId(Guid.Parse("aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa")),
            version: 1,
            "Archive invoices",
            "Move matching invoice files into Archive without overwriting anything.",
            new GrantId(Guid.Parse("bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb")),
            Now,
            examples,
            exclusions,
            answers);

    private static TeachingFileExample[] MoveExamples() =>
    [
        Example(
            1,
            ReconciledEffectKind.Moved,
            "Inbox\\invoice-1.pdf",
            "Archive\\invoice-1.pdf",
            "one"),
        Example(
            2,
            ReconciledEffectKind.Moved,
            "Inbox\\invoice-2.pdf",
            "Archive\\invoice-2.pdf",
            "two"),
    ];

    private static SkillUserAnswerContract[] ScopeAnswers() =>
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
        ReconciledEffectKind kind,
        string sourcePath,
        string destinationPath,
        string identity,
        string sourceVolume = "volume",
        string? destinationVolume = null)
    {
        FolderSnapshotEntry source = File(sourcePath, sourcePath, identity, sourceVolume);
        FolderSnapshotEntry destination = File(
            destinationPath,
            sourcePath,
            identity,
            destinationVolume ?? sourceVolume);
        ReconciledFileEffect effect = new(
            kind,
            sourcePath,
            destinationPath,
            source,
            destination,
            "reconcile.test_effect");
        return new TeachingFileExample(
            new ExampleId(Guid.Parse($"44444444-4444-4444-8444-{number:000000000000}")),
            new TeachingEpisodeId(Guid.Parse("33333333-3333-4333-8333-333333333333")),
            new GrantId(Guid.Parse("bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb")),
            new ResourceRootIdentity("winfs-v1:volume:compiler-root"),
            effect);
    }

    private static FolderSnapshotEntry File(
        string path,
        string content,
        string identity,
        string volume = "volume")
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
            volume,
            identity,
            SnapshotContentHashStatus.Computed,
            new ContentHash(Convert.ToHexStringLower(SHA256.HashData(bytes))));
    }
}
