using Tooltail.Contracts.Json;
using Tooltail.Contracts.Skills;

namespace Tooltail.Features.FileSkills.Tests.Skills;

internal static class SkillSpecFixture
{
    public static readonly DateTimeOffset Now =
        new(2026, 7, 16, 7, 0, 0, TimeSpan.Zero);

    public static SkillSpecContract Valid(
        IReadOnlyList<string>? extensions = null,
        IReadOnlyList<SkillUserAnswerContract>? answers = null) =>
        new()
        {
            SchemaVersion = ContractVersions.V1,
            SkillId = Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Version = 1,
            Name = "File invoice PDFs",
            Description = "Move matching PDF files into Archive without overwriting anything.",
            CreatedAt = Now,
            Compiler = new SkillCompilerContract
            {
                Kind = SkillCompilerKind.DeterministicTemplate,
                Version = "0.1.0",
            },
            Applicability = new SkillApplicabilityContract
            {
                RootGrantId = Guid.Parse("22222222-2222-4222-8222-222222222222"),
                Invocation = SkillInvocation.Manual,
                Match = new SkillMatchContract
                {
                    RegularFilesOnly = true,
                    OriginRelativeDirectory = "Inbox",
                    Extensions = extensions ?? [".pdf"],
                    Filename = new SkillFilenameMatchContract
                    {
                        Contains = "invoice",
                        CaseSensitive = false,
                    },
                },
            },
            Variables =
            [
                new SkillVariableContract
                {
                    Name = "originalStem",
                    Source = SkillVariableSource.OriginalStem,
                    Transforms = [],
                },
                new SkillVariableContract
                {
                    Name = "originalExtension",
                    Source = SkillVariableSource.OriginalExtension,
                    Transforms = [],
                },
            ],
            Steps =
            [
                new EnsureDirectoryStepContract
                {
                    StepId = "ensure_archive",
                    DirectoryTemplate = "Archive",
                },
                new MoveFileStepContract
                {
                    StepId = "move_file",
                    DestinationDirectoryTemplate = "Archive",
                    DestinationFileNameTemplate = "{{originalStem}}{{originalExtension}}",
                },
            ],
            Policy = new SkillPolicyContract
            {
                Collision = CollisionPolicy.Reject,
                RequireExactPlanApproval = true,
                AllowNetworkPaths = false,
                AllowReparsePoints = false,
                AllowOverwrite = false,
                SameVolumeOnly = true,
            },
            Verification = new SkillVerificationContract
            {
                AllPlannedStepsVerified = true,
                FailOnUnexpectedChange = true,
            },
            Provenance = new SkillProvenanceContract
            {
                TeachingEpisodeIds =
                [
                    Guid.Parse("33333333-3333-4333-8333-333333333333"),
                ],
                ExampleIds =
                [
                    Guid.Parse("44444444-4444-4444-8444-444444444441"),
                    Guid.Parse("44444444-4444-4444-8444-444444444442"),
                ],
                UserAnswers = answers ?? [],
                ParentVersion = null,
            },
            Compatibility = new SkillCompatibilityContract
            {
                ContractVersion = ContractVersions.V1,
                MinimumExecutorVersion = "0.1.0",
            },
        };
}
