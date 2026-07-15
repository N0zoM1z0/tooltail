using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;

namespace Tooltail.Domain.Tests;

public sealed class ExecutionRehydrationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 16, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void JournalRehydrationReplaysEveryPersistedTransition()
    {
        ExecutionJournal journal = VerifiedJournal();

        var restored = ExecutionJournal.Rehydrate(
            journal.ExecutionId,
            journal.PlanId,
            journal.PlanFingerprint,
            journal.Kind,
            journal.OperationPrimitives,
            journal.OperationInverseKinds,
            journal.RecoveryOperationPrimitives,
            journal.RecoveryOriginalStepSequences,
            journal.Events);

        Assert.True(restored.IsSuccess, restored.Error?.Code);
        Assert.Equal(journal.Events, restored.Value!.Events);
        Assert.Equal(
            StepRecoveryStatus.Verified,
            restored.Value.AssessStep(1).Status);
    }

    [Fact]
    public void JournalRehydrationRejectsSkippedAndMismatchedHistory()
    {
        ExecutionJournal journal = VerifiedJournal();
        ExecutionJournalEvent[] skippedCommit =
            [journal.Events[0], journal.Events[1], journal.Events[2], journal.Events[4]];
        ExecutionJournalEvent[] wrongExecution =
            [
                new ExecutionOpenedEvent(
                    new ExecutionId(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")),
                    Now.AddMinutes(2),
                    journal.PlanId,
                    journal.PlanFingerprint),
            ];

        var skipped = ExecutionJournal.Rehydrate(
            journal.ExecutionId,
            journal.PlanId,
            journal.PlanFingerprint,
            journal.Kind,
            journal.OperationPrimitives,
            journal.OperationInverseKinds,
            [],
            [],
            skippedCommit);
        var mismatched = ExecutionJournal.Rehydrate(
            journal.ExecutionId,
            journal.PlanId,
            journal.PlanFingerprint,
            journal.Kind,
            journal.OperationPrimitives,
            journal.OperationInverseKinds,
            [],
            [],
            wrongExecution);

        Assert.False(skipped.IsSuccess);
        Assert.Equal("journal.rehydrate_transition_invalid", skipped.Error?.Code);
        Assert.False(mismatched.IsSuccess);
        Assert.Equal("journal.rehydrate_open_invalid", mismatched.Error?.Code);
    }

    [Fact]
    public void JournalRehydrationRejectsOversizedPersistedOperationMetadata()
    {
        ExecutionJournal journal = VerifiedJournal();

        var restored = ExecutionJournal.Rehydrate(
            journal.ExecutionId,
            journal.PlanId,
            journal.PlanFingerprint,
            ExecutionJournalKind.Standard,
            Enumerable.Repeat(FilePrimitive.EnsureDirectory, 10_001),
            Enumerable.Repeat(JournalInverseKind.RemoveCreatedEntry, 10_001),
            [],
            [],
            [journal.Events[0]]);

        Assert.False(restored.IsSuccess);
        Assert.Equal("journal.rehydrate_shape_invalid", restored.Error?.Code);
    }

    [Fact]
    public void ExecutionPlanDefinitionRejectsMoreThanTenThousandOperations()
    {
        IEnumerable<PlannedFileOperation> operations = Enumerable.Range(1, 10_001)
            .Select(static sequence => new PlannedFileOperation(
                sequence,
                FilePrimitive.EnsureDirectory,
                sourceRelativePath: null,
                $"folder-{sequence}",
                sourceFingerprint: null,
                DestinationPrecondition.Absent,
                ExpectedSourceState.NotApplicable,
                ExpectedDestinationState.DirectoryPresent));

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => _ = new ExecutionPlanDefinition(
                new PlanId(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")),
                new SkillId(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")),
                new SkillVersionNumber(1),
                new SkillSpecificationHash(new string('a', 64)),
                new GrantId(Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc")),
                new ResourceRootIdentity("root-1"),
                [GrantCapability.CreateDirectory],
                Now,
                Now.AddHours(1),
                operations));

        Assert.Equal("operations", exception.ParamName);
    }

    [Fact]
    public void VerifiedReceiptCanBeRehydratedAfterItsStepWasSafelyRolledBack()
    {
        ExecutionJournal verified = VerifiedJournal();
        ExecutionId recoveryExecutionId = new(
            Guid.Parse("77777777-7777-4777-8777-777777777777"));
        ExecutionJournal rolledBack = verified.Append(
            new StepRolledBackEvent(
                verified.ExecutionId,
                6,
                Now.AddMinutes(7),
                1,
                recoveryExecutionId)).Value!;
        VerifiedEntryEvidence directory = new(
            VerifiedEntryKind.Directory,
            "volume-1",
            "entry-1",
            length: null,
            Now,
            Now.AddMinutes(1),
            attributes: 16,
            contentHash: null);
        VerifiedStepEvidence step = new(
            1,
            FilePrimitive.EnsureDirectory,
            sourceRelativePath: null,
            "sorted",
            destinationWasAbsent: true,
            directory);

        var restored = ExecutionReceipt.RehydrateVerified(
            new ReceiptId(Guid.Parse("88888888-8888-4888-8888-888888888888")),
            rolledBack,
            Now.AddMinutes(6).AddSeconds(30),
            Now.AddHours(1),
            [],
            [step]);

        Assert.True(restored.IsSuccess, restored.Error?.Code);
        Assert.Equal(1, restored.Value!.VerifiedStepCount);
    }

    [Theory]
    [InlineData(SkillLifecycleState.Draft, false)]
    [InlineData(SkillLifecycleState.Reliable, true)]
    [InlineData(SkillLifecycleState.Stale, false)]
    [InlineData(SkillLifecycleState.Stale, true)]
    public void SkillVersionRehydrationUsesLegalLifecycleTransitions(
        SkillLifecycleState lifecycle,
        bool wasApproved)
    {
        var restored = SkillVersion.Rehydrate(
            new SkillId(Guid.Parse("99999999-9999-4999-8999-999999999999")),
            new SkillVersionNumber(1),
            parent: null,
            new string('a', 64),
            "0.1.0",
            "0.1.0",
            lifecycle,
            Now,
            wasApproved);

        Assert.True(restored.IsSuccess, restored.Error?.Code);
        Assert.Equal(lifecycle, restored.Value!.Lifecycle);
    }

    [Fact]
    public void SkillVersionRehydrationRejectsMissingApprovalHistory()
    {
        var restored = SkillVersion.Rehydrate(
            new SkillId(Guid.Parse("99999999-9999-4999-8999-999999999999")),
            new SkillVersionNumber(1),
            parent: null,
            new string('a', 64),
            "0.1.0",
            "0.1.0",
            SkillLifecycleState.Practiced,
            Now,
            wasApproved: false);

        Assert.False(restored.IsSuccess);
        Assert.Equal("skill.rehydrate_state_invalid", restored.Error?.Code);
    }

    private static ExecutionJournal VerifiedJournal()
    {
        ExecutionPlan plan = Plan();
        ExecutionId executionId = new(
            Guid.Parse("66666666-6666-4666-8666-666666666666"));
        ExecutionJournal journal = ExecutionJournal.Open(
            executionId,
            plan,
            Now.AddMinutes(2));
        journal = journal.Append(
            new StepIntentRecordedEvent(
                executionId,
                2,
                Now.AddMinutes(3),
                1,
                FilePrimitive.EnsureDirectory,
                plan.Fingerprint,
                JournalInverseKind.RemoveCreatedEntry)).Value!;
        journal = journal.Append(
            new StepMutationObservedEvent(
                executionId,
                3,
                Now.AddMinutes(4),
                1)).Value!;
        journal = journal.Append(
            new StepCommittedEvent(
                executionId,
                4,
                Now.AddMinutes(5),
                1)).Value!;
        return journal.Append(
            new StepVerifiedEvent(
                executionId,
                5,
                Now.AddMinutes(6),
                1)).Value!;
    }

    private static ExecutionPlan Plan()
    {
        ExecutionPlanDefinition definition = new(
            new PlanId(Guid.Parse("11111111-1111-4111-8111-111111111111")),
            new SkillId(Guid.Parse("22222222-2222-4222-8222-222222222222")),
            new SkillVersionNumber(1),
            new SkillSpecificationHash(new string('a', 64)),
            new GrantId(Guid.Parse("33333333-3333-4333-8333-333333333333")),
            new ResourceRootIdentity("root-1"),
            [GrantCapability.CreateDirectory],
            Now,
            Now.AddHours(1),
            [
                new PlannedFileOperation(
                    1,
                    FilePrimitive.EnsureDirectory,
                    sourceRelativePath: null,
                    "sorted",
                    sourceFingerprint: null,
                    DestinationPrecondition.Absent,
                    ExpectedSourceState.NotApplicable,
                    ExpectedDestinationState.DirectoryPresent),
            ]);
        return new ExecutionPlan(definition, new PlanFingerprint(new string('b', 64)));
    }
}
