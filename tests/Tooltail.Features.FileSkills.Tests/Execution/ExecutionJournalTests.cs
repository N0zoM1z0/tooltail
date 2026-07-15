using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Features.FileSkills.Tests.Execution;

public sealed class ExecutionJournalTests
{
    private static readonly ExecutionId ExecutionId =
        new(Guid.Parse("66666666-6666-6666-6666-666666666666"));

    [Fact]
    public void CrashPrefixesProduceExplicitRecoveryStatuses()
    {
        ExecutionJournal opened = Open();
        ExecutionJournal intent = AppendIntent(opened, step: 1);
        ExecutionJournal observed = Append(
            intent,
            new StepMutationObservedEvent(ExecutionId, 3, At(3), stepSequence: 1));
        ExecutionJournal committed = Append(
            observed,
            new StepCommittedEvent(ExecutionId, 4, At(4), stepSequence: 1));
        ExecutionJournal verified = Append(
            committed,
            new StepVerifiedEvent(ExecutionId, 5, At(5), stepSequence: 1));
        ExecutionJournal recoveryRequired = Append(
            intent,
            new StepRecoveryRequiredEvent(
                ExecutionId,
                3,
                At(3),
                stepSequence: 1,
                "recovery.ambiguous_after_crash"));
        ExecutionJournal rolledBack = Append(
            recoveryRequired,
            new StepRolledBackEvent(
                ExecutionId,
                4,
                At(4),
                stepSequence: 1,
                new ExecutionId(Guid.Parse("77777777-7777-7777-7777-777777777777"))));

        AssertAssessment(opened, StepRecoveryStatus.NotStarted, requiresInspection: false);
        AssertAssessment(intent, StepRecoveryStatus.StartedUncommitted, requiresInspection: true);
        AssertAssessment(observed, StepRecoveryStatus.StartedUncommitted, requiresInspection: true);
        AssertAssessment(committed, StepRecoveryStatus.CommittedUnverified, requiresInspection: true);
        AssertAssessment(verified, StepRecoveryStatus.Verified, requiresInspection: false);
        AssertAssessment(recoveryRequired, StepRecoveryStatus.RecoveryRequired, requiresInspection: true);
        AssertAssessment(rolledBack, StepRecoveryStatus.RolledBack, requiresInspection: false);
    }

    [Fact]
    public void AppendReturnsNewJournalWithoutMutatingPriorPrefix()
    {
        ExecutionJournal opened = Open();

        ExecutionJournal intent = AppendIntent(opened, step: 1);

        Assert.Single(opened.Events);
        Assert.Equal(2, intent.Events.Count);
        Assert.IsType<ExecutionOpenedEvent>(opened.Events[0]);
        Assert.IsType<StepIntentRecordedEvent>(intent.Events[1]);
    }

    [Fact]
    public void VerifiedStepCanOnlyBeMarkedRolledBackByDistinctRecoveryExecution()
    {
        ExecutionJournal verified = CompleteStep(Open(), step: 1, firstEventSequence: 2);
        ExecutionId recoveryExecutionId =
            new(Guid.Parse("77777777-7777-4777-8777-777777777777"));

        var linked = verified.Append(
            new StepRolledBackEvent(
                ExecutionId,
                verified.Events.Count + 1L,
                At(verified.Events.Count + 1),
                stepSequence: 1,
                recoveryExecutionId));
        Assert.True(linked.IsSuccess);
        Assert.Equal(StepRecoveryStatus.RolledBack, linked.Value!.AssessStep(1).Status);
        Assert.Throws<ArgumentException>(
            () => new StepRolledBackEvent(
                ExecutionId,
                verified.Events.Count + 1L,
                At(verified.Events.Count + 1),
                stepSequence: 1,
                ExecutionId));
    }

    [Fact]
    public void CommitCannotAppearWithoutObservedMutation()
    {
        ExecutionJournal intent = AppendIntent(Open(), step: 1);

        var result = intent.Append(
            new StepCommittedEvent(ExecutionId, 3, At(3), stepSequence: 1));

        Assert.False(result.IsSuccess);
        Assert.Equal("journal.transition_invalid", result.Error?.Code);
        Assert.Equal(StepRecoveryStatus.StartedUncommitted, intent.AssessStep(1).Status);
    }

    [Fact]
    public void NextStepCannotStartUntilPreviousStepIsVerified()
    {
        ExecutionJournal firstIntent = AppendIntent(Open(), step: 1);

        var result = firstIntent.Append(
            new StepIntentRecordedEvent(
                ExecutionId,
                3,
                At(3),
                stepSequence: 2,
                FilePrimitive.MoveFile,
                firstIntent.PlanFingerprint,
                JournalInverseKind.MoveBack));

        Assert.False(result.IsSuccess);
        Assert.Equal("journal.transition_invalid", result.Error?.Code);
    }

    [Fact]
    public void IntentMustMatchPlanPrimitiveAndFingerprint()
    {
        ExecutionJournal opened = Open();

        var wrongPrimitive = opened.Append(
            new StepIntentRecordedEvent(
                ExecutionId,
                2,
                At(2),
                stepSequence: 1,
                FilePrimitive.CopyFile,
                opened.PlanFingerprint,
                JournalInverseKind.RemoveCreatedEntry));
        var wrongFingerprint = opened.Append(
            new StepIntentRecordedEvent(
                ExecutionId,
                2,
                At(2),
                stepSequence: 1,
                FilePrimitive.EnsureDirectory,
                new PlanFingerprint(new string('f', 64)),
                JournalInverseKind.RemoveCreatedEntry));

        Assert.Equal("journal.transition_invalid", wrongPrimitive.Error?.Code);
        Assert.Equal("journal.transition_invalid", wrongFingerprint.Error?.Code);
    }

    [Fact]
    public void FailureAndRecoveryMarkersRetainOnlySafeReasonCodes()
    {
        ExecutionJournal intent = AppendIntent(Open(), step: 1);
        ExecutionJournal failed = Append(
            intent,
            new StepFailedEvent(
                ExecutionId,
                3,
                At(3),
                stepSequence: 1,
                "filesystem.access_denied"));
        ExecutionJournal recovery = Append(
            failed,
            new StepRecoveryRequiredEvent(
                ExecutionId,
                4,
                At(4),
                stepSequence: 1,
                "recovery.inspect_actual_state"));

        StepRecoveryAssessment assessment = recovery.AssessStep(1);

        Assert.Equal("filesystem.access_denied", assessment.FailureCode);
        Assert.Equal("recovery.inspect_actual_state", assessment.RecoveryReasonCode);
        Assert.Throws<ArgumentException>(
            () => new StepFailedEvent(
                ExecutionId,
                3,
                At(3),
                stepSequence: 1,
                "C:\\Users\\Private\\file.txt"));
    }

    [Fact]
    public void SuccessfulReceiptRequiresEveryStepVerified()
    {
        ExecutionJournal incomplete = CompleteStep(Open(), step: 1, firstEventSequence: 2);
        var rejected = ExecutionReceipt.CreateVerified(
            new ReceiptId(Guid.Parse("88888888-8888-8888-8888-888888888888")),
            incomplete,
            At(6),
            At(60));
        ExecutionJournal complete = CompleteStep(incomplete, step: 2, firstEventSequence: 6);
        var receipt = ExecutionReceipt.CreateVerified(
            new ReceiptId(Guid.Parse("88888888-8888-8888-8888-888888888888")),
            complete,
            At(10),
            At(60));

        Assert.False(rejected.IsSuccess);
        Assert.Equal("receipt.execution_not_verified", rejected.Error?.Code);
        Assert.True(receipt.IsSuccess);
        Assert.Equal(2, receipt.Value!.VerifiedStepCount);
        Assert.Empty(receipt.Value.ResidualEffectCodes);
        Assert.Equal(complete.PlanFingerprint, receipt.Value.PlanFingerprint);
    }

    [Fact]
    public void FileReceiptEvidenceMustMatchEveryExactPlanStep()
    {
        ExecutionPlan plan = ExecutionPlanFixture.Plan();
        ExecutionJournal complete = CompleteStep(
            CompleteStep(Open(), step: 1, firstEventSequence: 2),
            step: 2,
            firstEventSequence: 6);
        VerifiedEntryEvidence directory = new(
            VerifiedEntryKind.Directory,
            "volume-a",
            "directory-a",
            null,
            At(1),
            At(1),
            attributes: 0,
            contentHash: null);
        VerifiedEntryEvidence file = new(
            VerifiedEntryKind.File,
            "volume-a",
            "file-id-01",
            128,
            At(1),
            ExecutionPlanFixture.Now.AddMinutes(-5),
            attributes: 0,
            new ContentHash(new string('b', 64)));
        VerifiedStepEvidence[] exact =
        [
            new(
                1,
                FilePrimitive.EnsureDirectory,
                null,
                "Archive\\2026",
                destinationWasAbsent: true,
                directory),
            new(
                2,
                FilePrimitive.MoveFile,
                "Inbox\\Report.txt",
                "Archive\\2026\\Report.txt",
                destinationWasAbsent: true,
                file),
        ];

        var receipt = ExecutionReceipt.CreateVerified(
            new ReceiptId(Guid.Parse("88888888-8888-8888-8888-888888888888")),
            plan,
            complete,
            At(10),
            At(60),
            exact);
        var mismatched = ExecutionReceipt.CreateVerified(
            new ReceiptId(Guid.Parse("88888888-8888-8888-8888-888888888888")),
            plan,
            complete,
            At(10),
            At(60),
            [
                exact[0],
                new VerifiedStepEvidence(
                    2,
                    FilePrimitive.MoveFile,
                    "Inbox\\Report.txt",
                    "Other\\Report.txt",
                    destinationWasAbsent: true,
                    file),
            ]);

        Assert.True(receipt.IsSuccess);
        Assert.Equal(exact, receipt.Value!.VerifiedSteps);
        Assert.Equal("receipt.evidence_plan_mismatch", mismatched.Error?.Code);
    }

    [Fact]
    public void JournalRejectsWrongExecutionSequenceAndRegressedTime()
    {
        ExecutionJournal opened = Open();

        var wrongExecution = opened.Append(
            new StepIntentRecordedEvent(
                new ExecutionId(Guid.Parse("99999999-9999-9999-9999-999999999999")),
                2,
                At(2),
                1,
                FilePrimitive.EnsureDirectory,
                opened.PlanFingerprint,
                JournalInverseKind.RemoveCreatedEntry));
        var skippedSequence = opened.Append(
            new StepIntentRecordedEvent(
                ExecutionId,
                3,
                At(3),
                1,
                FilePrimitive.EnsureDirectory,
                opened.PlanFingerprint,
                JournalInverseKind.RemoveCreatedEntry));
        var regressedTime = opened.Append(
            new StepIntentRecordedEvent(
                ExecutionId,
                2,
                ExecutionPlanFixture.Now,
                1,
                FilePrimitive.EnsureDirectory,
                opened.PlanFingerprint,
                JournalInverseKind.RemoveCreatedEntry));

        Assert.Equal("journal.execution_mismatch", wrongExecution.Error?.Code);
        Assert.Equal("journal.event_sequence_invalid", skippedSequence.Error?.Code);
        Assert.Equal("journal.time_regressed", regressedTime.Error?.Code);
    }

    private static ExecutionJournal Open() =>
        ExecutionJournal.Open(
            ExecutionId,
            ExecutionPlanFixture.Plan(),
            At(1));

    private static ExecutionJournal AppendIntent(ExecutionJournal journal, int step) =>
        Append(
            journal,
            new StepIntentRecordedEvent(
                ExecutionId,
                journal.Events.Count + 1L,
                At(journal.Events.Count + 1),
                step,
                journal.OperationPrimitives[step - 1],
                journal.PlanFingerprint,
                journal.OperationInverseKinds[step - 1]));

    private static ExecutionJournal CompleteStep(
        ExecutionJournal journal,
        int step,
        int firstEventSequence)
    {
        ExecutionJournal intent = Append(
            journal,
            new StepIntentRecordedEvent(
                ExecutionId,
                firstEventSequence,
                At(firstEventSequence),
                step,
                journal.OperationPrimitives[step - 1],
                journal.PlanFingerprint,
                journal.OperationInverseKinds[step - 1]));
        ExecutionJournal observed = Append(
            intent,
            new StepMutationObservedEvent(
                ExecutionId,
                firstEventSequence + 1L,
                At(firstEventSequence + 1),
                step));
        ExecutionJournal committed = Append(
            observed,
            new StepCommittedEvent(
                ExecutionId,
                firstEventSequence + 2L,
                At(firstEventSequence + 2),
                step));
        return Append(
            committed,
            new StepVerifiedEvent(
                ExecutionId,
                firstEventSequence + 3L,
                At(firstEventSequence + 3),
                step));
    }

    private static ExecutionJournal Append(
        ExecutionJournal journal,
        ExecutionJournalEvent journalEvent)
    {
        var result = journal.Append(journalEvent);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        return result.Value!;
    }

    private static DateTimeOffset At(int seconds) =>
        ExecutionPlanFixture.Now.AddMinutes(2).AddSeconds(seconds);

    private static void AssertAssessment(
        ExecutionJournal journal,
        StepRecoveryStatus expected,
        bool requiresInspection)
    {
        StepRecoveryAssessment assessment = journal.AssessStep(1);
        Assert.Equal(expected, assessment.Status);
        Assert.Equal(requiresInspection, assessment.RequiresFileSystemInspection);
    }
}
