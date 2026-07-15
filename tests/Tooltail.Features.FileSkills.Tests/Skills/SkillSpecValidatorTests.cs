using Tooltail.Contracts.Skills;
using Tooltail.Features.FileSkills.Skills;

namespace Tooltail.Features.FileSkills.Tests.Skills;

public sealed class SkillSpecValidatorTests
{
    [Fact]
    public void ReviewedClosedSkillSpecIsSemanticallyValid()
    {
        SkillValidationResult result = SkillSpecValidator.Validate(SkillSpecFixture.Valid());

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void UnsafePolicyAndUnknownTemplateVariableReturnFieldErrors()
    {
        SkillSpecContract baseline = SkillSpecFixture.Valid();
        SkillSpecContract unsafeSpec = baseline with
        {
            Policy = baseline.Policy with { AllowOverwrite = true },
            Steps =
            [
                new MoveFileStepContract
                {
                    StepId = "move_file",
                    DestinationDirectoryTemplate = "Archive",
                    DestinationFileNameTemplate = "{{unknown}}.pdf",
                },
            ],
        };

        SkillValidationResult result = SkillSpecValidator.Validate(unsafeSpec);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static error => error.Code == "skill.policy_unsafe");
        Assert.Contains(result.Errors, static error => error.Code == "template.variable_undefined");
    }

    [Theory]
    [InlineData("../outside", "path.traversal")]
    [InlineData("Archive\\Nested", "template.invalid_shape")]
    [InlineData("CON", "path.reserved_name")]
    [InlineData("Archive/{{originalStem", "template.invalid_syntax")]
    public void UnsafeTemplatesFailClosed(string template, string expectedCode)
    {
        SkillSpecContract baseline = SkillSpecFixture.Valid();
        MoveFileStepContract move = (MoveFileStepContract)baseline.Steps[1];
        SkillSpecContract changed = baseline with
        {
            Steps =
            [
                baseline.Steps[0],
                move with { DestinationDirectoryTemplate = template },
            ],
        };

        SkillValidationResult result = SkillSpecValidator.Validate(changed);

        Assert.Contains(result.Errors, error => error.Code == expectedCode);
    }

    [Fact]
    public void SafeRegexMustBeAnchoredAndNonBacktrackingCompatible()
    {
        SkillSpecContract baseline = SkillSpecFixture.Valid();
        SkillSpecContract changed = baseline with
        {
            Applicability = baseline.Applicability with
            {
                Match = baseline.Applicability.Match with
                {
                    Filename = new SkillFilenameMatchContract
                    {
                        SafeRegex = "(invoice)+$",
                        CaseSensitive = false,
                    },
                },
            },
        };

        SkillValidationResult result = SkillSpecValidator.Validate(changed);

        Assert.Contains(result.Errors, static error => error.Code == "skill.regex_not_anchored");
    }

    [Fact]
    public void RegexCaptureVariableMustNameADeclaredMatcherGroup()
    {
        SkillSpecContract baseline = SkillSpecFixture.Valid();
        SkillSpecContract changed = baseline with
        {
            Applicability = baseline.Applicability with
            {
                Match = baseline.Applicability.Match with
                {
                    Filename = new SkillFilenameMatchContract
                    {
                        SafeRegex = "\\Ainvoice-(?<number>[0-9]+)\\.pdf\\z",
                        CaseSensitive = false,
                    },
                },
            },
            Variables =
            [
                .. baseline.Variables,
                new SkillVariableContract
                {
                    Name = "missingCapture",
                    Source = SkillVariableSource.RegexCapture,
                    Argument = "missing",
                    Transforms = [],
                },
            ],
        };

        SkillValidationResult result = SkillSpecValidator.Validate(changed);

        Assert.Contains(
            result.Errors,
            static error => error.Code == "skill.regex_capture_group_missing");
    }

    [Fact]
    public void ParentVersionMustBeOlderThanTheCurrentVersion()
    {
        SkillSpecContract baseline = SkillSpecFixture.Valid();
        SkillSpecContract changed = baseline with
        {
            Provenance = baseline.Provenance with { ParentVersion = baseline.Version },
        };

        SkillValidationResult result = SkillSpecValidator.Validate(changed);

        Assert.Contains(
            result.Errors,
            static error => error.Code == "skill.parent_version_invalid");
    }
}
