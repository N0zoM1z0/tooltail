using System.Text;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Execution;

namespace Tooltail.Features.FileSkills.Tests.Execution;

public sealed class CanonicalRecoveryPlanTests
{
    private static readonly DateTimeOffset Now = ExecutionPlanFixture.Now;

    [Fact]
    public void RecoveryPlanFingerprintIsDeterministicAndGoldenLocked()
    {
        RecoveryPlanDefinition definition = Definition();
        RecoveryPlanDefinition reorderedCapabilities = Definition(
            capabilities: Capabilities.Reverse());

        RecoveryPlan first = CanonicalRecoveryPlan.Create(definition).Value!;
        RecoveryPlan second = CanonicalRecoveryPlan.Create(reorderedCapabilities).Value!;

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(
            "cff09146b448344e1bd1bd0361a5c86df21d603b12c82445a208f105c381bd7c",
            first.Fingerprint.Value);
        string canonical = Encoding.UTF8.GetString(
            CanonicalRecoveryPlan.Encode(definition));
        Assert.Contains("\"contractVersion\":\"tooltail.recovery-plan/1\"", canonical);
        Assert.Contains("\"recoveryPrimitive\":\"move_back\"", canonical);
        Assert.Contains("\"originalStepSequence\":2", canonical);
    }

    [Fact]
    public void UnsafeRecoveryPathIsRejectedEvenWhenFingerprintCanBeComputed()
    {
        RecoveryPlanDefinition definition = Definition(
            source: "C:\\outside.txt");

        var result = CanonicalRecoveryPlan.Create(definition);

        Assert.False(result.IsSuccess);
        Assert.Equal("path.drive_relative", result.Error?.Code);
    }

    [Fact]
    public void RecoveryJournalAcceptsOnlyItsExactRecoveryIntent()
    {
        RecoveryPlan plan = CanonicalRecoveryPlan.Create(Definition()).Value!;
        ExecutionId executionId =
            new(Guid.Parse("99999999-9999-4999-8999-999999999999"));
        ExecutionJournal opened = ExecutionJournal.OpenRecovery(
            executionId,
            plan,
            Now.AddMinutes(2));
        var wrong = opened.Append(
            new RecoveryStepIntentRecordedEvent(
                executionId,
                2,
                Now.AddMinutes(2),
                1,
                RecoveryPrimitive.RenameBack,
                originalStepSequence: 2,
                plan.Fingerprint));
        var exact = opened.Append(
            new RecoveryStepIntentRecordedEvent(
                executionId,
                2,
                Now.AddMinutes(2),
                1,
                RecoveryPrimitive.MoveBack,
                originalStepSequence: 2,
                plan.Fingerprint));

        Assert.Equal("journal.transition_invalid", wrong.Error?.Code);
        Assert.True(exact.IsSuccess);
        Assert.Equal(
            StepRecoveryStatus.StartedUncommitted,
            exact.Value!.AssessStep(1).Status);
    }

    [Fact]
    public void InternalRemovalNeverEntersLearnedFilePrimitiveVocabulary()
    {
        Assert.DoesNotContain(
            Enum.GetNames<FilePrimitive>(),
            name => name.Contains("Remove", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Delete", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            RecoveryPrimitive.RemoveCreatedEntry,
            Enum.GetValues<RecoveryPrimitive>());
    }

    [Fact]
    public void RecoveryPlanIdentityMustDifferFromOriginalPlanIdentity()
    {
        Assert.Throws<ArgumentException>(
            () => Definition(planId: ExecutionPlanFixture.PlanId));
    }

    private static RecoveryPlanDefinition Definition(
        IEnumerable<GrantCapability>? capabilities = null,
        string source = "Archive\\2026\\Report.txt",
        PlanId? planId = null)
    {
        VerifiedEntryEvidence evidence = new(
            VerifiedEntryKind.File,
            "volume-a",
            "file-id-01",
            128,
            Now.AddMinutes(-10),
            Now.AddMinutes(-5),
            attributes: 32,
            new ContentHash(new string('b', 64)));
        return new RecoveryPlanDefinition(
            planId ?? new PlanId(Guid.Parse("66666666-6666-4666-8666-666666666666")),
            new ExecutionId(Guid.Parse("77777777-7777-4777-8777-777777777777")),
            ExecutionPlanFixture.PlanId,
            new PlanFingerprint(new string('c', 64)),
            ExecutionPlanFixture.SkillId,
            new SkillVersionNumber(3),
            new SkillSpecificationHash(new string('a', 64)),
            ExecutionPlanFixture.GrantId,
            new ResourceRootIdentity("winfs-v1:volume-a:root-a"),
            capabilities ?? Capabilities,
            Now,
            Now.AddMinutes(30),
            [
                new PlannedRecoveryOperation(
                    1,
                    originalStepSequence: 2,
                    FilePrimitive.MoveFile,
                    RecoveryPrimitive.MoveBack,
                    source,
                    "Inbox\\Report.txt",
                    evidence,
                    originalDestinationWasAbsent: true),
            ]);
    }

    private static readonly GrantCapability[] Capabilities =
    [
        GrantCapability.Enumerate,
        GrantCapability.ReadMetadata,
        GrantCapability.ReadContentHash,
        GrantCapability.MoveWithinRoot,
    ];
}
