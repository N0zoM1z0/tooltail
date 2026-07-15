using Tooltail.Contracts.Skills;
using Tooltail.Domain.Execution;
using Tooltail.Features.FileSkills.Skills;
using Tooltail.Features.FileSkills.Snapshots;

namespace Tooltail.Features.FileSkills.Tests.Skills;

public sealed class SkillTemplateAndCanonicalTests
{
    [Fact]
    public void TypedVariablesRenderOneNormalizedWindowsRelativeDestination()
    {
        FolderSnapshotEntry source = new(
            "Inbox\\Quarterly Report.PDF",
            SnapshotEntryKind.File,
            10,
            SkillSpecFixture.Now,
            SkillSpecFixture.Now,
            FileAttributes.Archive,
            isReparsePoint: false,
            "volume",
            "entry",
            SnapshotContentHashStatus.NotPermitted,
            contentHash: null);
        SkillVariableContract[] definitions =
        [
            new SkillVariableContract
            {
                Name = "stem",
                Source = SkillVariableSource.OriginalStem,
                Transforms = [SkillVariableTransform.SlugHyphen],
            },
            new SkillVariableContract
            {
                Name = "extension",
                Source = SkillVariableSource.OriginalExtension,
                Transforms = [SkillVariableTransform.Lowercase],
            },
        ];

        SkillTemplateResult binding = SkillTemplateEngine.BindVariables(
            definitions,
            source,
            filenameMatch: null,
            userParameters: null,
            out IReadOnlyDictionary<string, string>? values);
        SkillTemplateResult rendered = SkillTemplateEngine.Render(
            "Archive/{{stem}}{{extension}}",
            SkillTemplateKind.RelativePath,
            values!,
            "steps[0]");

        Assert.True(binding.IsSuccess);
        Assert.True(rendered.IsSuccess, rendered.Error?.Code);
        Assert.Equal("Archive\\Quarterly-Report.pdf", rendered.Value);
    }

    [Fact]
    public void RenderedTraversalFromUserParameterIsRejectedAfterBinding()
    {
        FolderSnapshotEntry source = new(
            "file.txt",
            SnapshotEntryKind.File,
            1,
            SkillSpecFixture.Now,
            SkillSpecFixture.Now,
            FileAttributes.Archive,
            isReparsePoint: false,
            "volume",
            "entry",
            SnapshotContentHashStatus.NotPermitted,
            contentHash: null);
        SkillVariableContract[] definitions =
        [
            new SkillVariableContract
            {
                Name = "folder",
                Source = SkillVariableSource.UserParameter,
                Argument = "folder",
                Transforms = [],
            },
        ];
        SkillTemplateEngine.BindVariables(
            definitions,
            source,
            filenameMatch: null,
            new Dictionary<string, string> { ["folder"] = ".." },
            out IReadOnlyDictionary<string, string>? values);

        SkillTemplateResult rendered = SkillTemplateEngine.Render(
            "{{folder}}/file.txt",
            SkillTemplateKind.RelativePath,
            values!,
            "steps[0]");

        Assert.False(rendered.IsSuccess);
        Assert.Equal("path.traversal", rendered.Error?.Code);
    }

    [Fact]
    public void TransformThatErasesARequiredVariableFailsBinding()
    {
        FolderSnapshotEntry source = new(
            "---.pdf",
            SnapshotEntryKind.File,
            1,
            SkillSpecFixture.Now,
            SkillSpecFixture.Now,
            FileAttributes.Archive,
            isReparsePoint: false,
            "volume",
            "entry",
            SnapshotContentHashStatus.NotPermitted,
            contentHash: null);
        SkillVariableContract[] definitions =
        [
            new SkillVariableContract
            {
                Name = "stem",
                Source = SkillVariableSource.OriginalStem,
                Transforms = [SkillVariableTransform.SlugHyphen],
            },
        ];

        SkillTemplateResult result = SkillTemplateEngine.BindVariables(
            definitions,
            source,
            filenameMatch: null,
            userParameters: null,
            out IReadOnlyDictionary<string, string>? values);

        Assert.False(result.IsSuccess);
        Assert.Null(values);
        Assert.Equal("template.variable_value_invalid", result.Error?.Code);
    }

    [Fact]
    public void CanonicalHashIgnoresSetOrderingButChangesWithExecutableTemplate()
    {
        SkillSpecContract first = SkillSpecFixture.Valid(extensions: [".txt", ".pdf"]);
        SkillSpecContract reordered = first with
        {
            Applicability = first.Applicability with
            {
                Match = first.Applicability.Match with
                {
                    Extensions = [".pdf", ".txt"],
                },
            },
            Provenance = first.Provenance with
            {
                ExampleIds = first.Provenance.ExampleIds.Reverse().ToArray(),
            },
        };
        MoveFileStepContract move = (MoveFileStepContract)first.Steps[1];
        SkillSpecContract changed = first with
        {
            Steps =
            [
                first.Steps[0],
                move with { DestinationDirectoryTemplate = "Other" },
            ],
        };

        SkillSpecificationHash firstHash = CanonicalSkillSpec.ComputeHash(first);

        Assert.Equal(firstHash, CanonicalSkillSpec.ComputeHash(reordered));
        Assert.NotEqual(firstHash, CanonicalSkillSpec.ComputeHash(changed));
        Assert.Equal(
            "617023e0b11f33d6b1008ccb4f430a79edfface6916c843f60c8675383a64abb",
            firstHash.Value);
    }
}
