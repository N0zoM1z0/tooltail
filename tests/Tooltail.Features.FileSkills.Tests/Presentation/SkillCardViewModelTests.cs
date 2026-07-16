using Tooltail.Contracts.Skills;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Presentation;
using Tooltail.Features.FileSkills.Skills;
using Tooltail.Features.FileSkills.Tests.Skills;

namespace Tooltail.Features.FileSkills.Tests.Presentation;

public sealed class SkillCardViewModelTests
{
    [Fact]
    public void CardExposesEveryExactSectionWithoutOpaqueConfidence()
    {
        SkillSpecContract specification = SkillSpecFixture.Valid();
        SkillSpecificationHash hash = CanonicalSkillSpec.ComputeHash(specification);
        SkillCardEvidence dryRun = new(
            SkillCardEvidenceKind.DryRunPassed,
            "planner.ready",
            SkillSpecFixture.Now.AddMinutes(1),
            hash,
            new PlanFingerprint(new string('a', 64)));
        SkillCardRequest request = new(
            specification,
            SkillLifecycleState.Draft,
            @"D:\TooltailLab (granted local folder)",
            Capabilities(),
            samples:
            [
                new SkillCardSample(
                    "Inbox\\invoice-1.pdf",
                    "Archive\\invoice-1.pdf"),
            ],
            evidence: [dryRun],
            canDisableSkill: true,
            canDeleteLocalHistory: true);

        SkillCardViewModel card = SkillCardBuilder.Build(request);

        Assert.Equal(specification.Name, card.EditableName);
        Assert.Equal(specification.SkillId.ToString("D"), card.SkillId);
        Assert.Equal(hash.Value, card.SpecificationHash);
        Assert.Contains(card.MatchPredicates, static fact => fact.Label == "Extensions");
        Assert.Equal(2, card.Variables.Count);
        Assert.Equal(2, card.Operations.Count);
        Assert.NotEmpty(card.AlwaysRules);
        Assert.Contains(card.NeverRules, static rule => rule.Contains("shell", StringComparison.Ordinal));
        Assert.NotEmpty(card.AskRules);
        Assert.NotEmpty(card.SuccessRules);
        Assert.NotEmpty(card.LearnedFrom);
        Assert.NotEmpty(card.Compatibility);
        Assert.Single(card.Samples);
        Assert.Single(card.Evidence);
        Assert.Empty(card.SemanticDiff);
        Assert.Equal(6, card.Actions.Count);
        Assert.True(card.ApproveAction.IsEnabled);
        Assert.True(card.DisableAction.IsEnabled);
        Assert.True(card.DeleteLocalHistoryAction.IsEnabled);
        Assert.DoesNotContain(
            "confidence",
            string.Join(
                " ",
                card.MatchPredicates.Select(static fact => $"{fact.Label} {fact.Value}")),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnimplementedGranularMaintenanceActionsAreTruthfullyDisabled()
    {
        SkillCardViewModel card = SkillCardBuilder.Build(new SkillCardRequest(
            SkillSpecFixture.Valid(),
            SkillLifecycleState.Draft,
            "TooltailLab",
            Capabilities()));

        Assert.False(card.DisableAction.IsEnabled);
        Assert.Contains(
            "not available in v0.1",
            card.DisableAction.DisabledReason,
            StringComparison.Ordinal);
        Assert.False(card.DeleteLocalHistoryAction.IsEnabled);
        Assert.Contains(
            "Retention or recovery policy",
            card.DeleteLocalHistoryAction.DisabledReason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovalRequiresPassingEvidenceForThisExactSpecificationHash()
    {
        SkillSpecContract specification = SkillSpecFixture.Valid();
        SkillSpecificationHash otherHash = new(new string('b', 64));
        SkillCardEvidence staleDryRun = new(
            SkillCardEvidenceKind.DryRunPassed,
            "planner.ready",
            SkillSpecFixture.Now,
            otherHash,
            new PlanFingerprint(new string('c', 64)));

        SkillCardViewModel card = SkillCardBuilder.Build(new SkillCardRequest(
            specification,
            SkillLifecycleState.Draft,
            "TooltailLab",
            Capabilities(),
            evidence: [staleDryRun]));

        Assert.False(card.ApproveAction.IsEnabled);
        Assert.Equal(
            "This exact version needs a passing dry-run or rehearsal.",
            card.ApproveAction.DisabledReason);
        Assert.False(Assert.Single(card.Evidence).IsCurrentVersion);
        Assert.Contains("No current-version", card.EvidenceSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingGrantActionIsVisibleAndDisablesApprovalAndRehearsal()
    {
        SkillSpecContract specification = SkillSpecFixture.Valid();
        SkillSpecificationHash hash = CanonicalSkillSpec.ComputeHash(specification);
        SkillCardEvidence dryRun = new(
            SkillCardEvidenceKind.DryRunPassed,
            "planner.ready",
            SkillSpecFixture.Now,
            hash,
            new PlanFingerprint(new string('d', 64)));

        SkillCardViewModel card = SkillCardBuilder.Build(new SkillCardRequest(
            specification,
            SkillLifecycleState.Draft,
            "TooltailLab",
            [GrantCapability.Enumerate, GrantCapability.ReadMetadata],
            evidence: [dryRun]));

        Assert.Contains("missing required actions", card.GrantedActions, StringComparison.Ordinal);
        Assert.Contains("create directories", card.GrantedActions, StringComparison.Ordinal);
        Assert.Contains("move files within root", card.GrantedActions, StringComparison.Ordinal);
        Assert.False(card.RehearseAction.IsEnabled);
        Assert.False(card.ApproveAction.IsEnabled);
        Assert.Contains("current grant is missing", card.ApproveAction.DisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void SemanticDiffIsFieldLocalizedAndDeterministic()
    {
        SkillSpecContract parent = SkillSpecFixture.Valid();
        MoveFileStepContract parentMove = Assert.IsType<MoveFileStepContract>(
            parent.Steps[1]);
        SkillSpecContract current = parent with
        {
            Version = 2,
            Name = "File reviewed invoice PDFs",
            CreatedAt = parent.CreatedAt.AddMinutes(1),
            Steps =
            [
                new EnsureDirectoryStepContract
                {
                    StepId = "ensure_reviewed",
                    DirectoryTemplate = "Reviewed",
                },
                parentMove with { DestinationDirectoryTemplate = "Reviewed" },
            ],
            Provenance = parent.Provenance with { ParentVersion = 1 },
        };
        SkillCardRequest request = new(
            current,
            SkillLifecycleState.Draft,
            "TooltailLab",
            Capabilities(),
            parentSpecification: parent);

        SkillCardViewModel first = SkillCardBuilder.Build(request);
        SkillCardViewModel second = SkillCardBuilder.Build(request);

        Assert.Equal(first.SemanticDiff, second.SemanticDiff);
        Assert.Collection(
            first.SemanticDiff,
            change =>
            {
                Assert.Equal("metadata.name", change.Field);
                Assert.Equal(SkillCardSemanticChangeKind.Changed, change.Kind);
            },
            change =>
            {
                Assert.Equal("steps", change.Field);
                Assert.Contains("Reviewed", change.After, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void EditableNameReportsTextualValidationWithoutChangingSkillIdentity()
    {
        SkillCardViewModel card = SkillCardBuilder.Build(new SkillCardRequest(
            SkillSpecFixture.Valid(),
            SkillLifecycleState.Draft,
            "TooltailLab",
            Capabilities()));
        List<string?> changed = [];
        card.PropertyChanged += (_, eventArgs) => changed.Add(eventArgs.PropertyName);

        card.EditableName = string.Empty;

        Assert.False(card.HasValidEditableName);
        Assert.True(card.HasUnsavedNameEdit);
        Assert.Equal("Name is required.", card.NameValidationMessage);
        Assert.Contains(nameof(SkillCardViewModel.EditableName), changed);
        Assert.Equal(
            SkillSpecFixture.Valid().SkillId.ToString("D"),
            card.SkillId);
    }

    [Fact]
    public void DeclaredParentCannotBeOmittedOrSubstituted()
    {
        SkillSpecContract parent = SkillSpecFixture.Valid();
        SkillSpecContract current = parent with
        {
            Version = 2,
            CreatedAt = parent.CreatedAt.AddMinutes(1),
            Provenance = parent.Provenance with { ParentVersion = 1 },
        };
        SkillSpecContract wrongParent = parent with
        {
            SkillId = Guid.Parse("99999999-9999-4999-8999-999999999999"),
        };

        Assert.Throws<ArgumentException>(() => SkillCardBuilder.Build(
            new SkillCardRequest(
                current,
                SkillLifecycleState.Draft,
                "TooltailLab",
                Capabilities())));
        Assert.Throws<ArgumentException>(() => SkillCardBuilder.Build(
            new SkillCardRequest(
                current,
                SkillLifecycleState.Draft,
                "TooltailLab",
                Capabilities(),
                parentSpecification: wrongParent)));
    }

    private static GrantCapability[] Capabilities() =>
    [
        GrantCapability.Enumerate,
        GrantCapability.ReadMetadata,
        GrantCapability.ReadContentHash,
        GrantCapability.CreateDirectory,
        GrantCapability.MoveWithinRoot,
    ];
}
