using Microsoft.Data.Sqlite;
using Tooltail.Application.Abstractions;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Infrastructure.Sqlite.Tests;

public sealed class SqliteExecutionJournalStoreTests
{
    [Fact]
    public async Task StandardAndUndoReceiptsRoundTripAfterRestartThroughDomainReplay()
    {
        using SqlitePersistenceTestContext context =
            await SqlitePersistenceTestContext.CreateAsync();
        await context.SeedAuthorityAndSkillAsync();
        ExecutionPlan plan = context.CreateStandardPlan(
            Guid.Parse("44444444-4444-4444-8444-444444444444"));
        StateWriteResult storedPlan = await context.StateStore.StoreExecutionPlanAsync(
            plan,
            SqlitePersistenceTestContext.CanonicalJson(plan));
        Assert.True(storedPlan.IsSuccess, storedPlan.FailureCode);

        PlanApproval active = PlanApproval.Issue(
            new ApprovalId(Guid.Parse("55555555-5555-4555-8555-555555555555")),
            plan,
            SqlitePersistenceTestContext.Now.AddMinutes(3),
            SqlitePersistenceTestContext.Now.AddMinutes(30));
        await AssertApprovalStoredAsync(context, active);
        PlanApproval consumed = active.Consume(
            plan,
            SqlitePersistenceTestContext.Now.AddMinutes(4)).Value!;
        ExecutionId originalExecutionId = new(
            Guid.Parse("66666666-6666-4666-8666-666666666666"));
        ExecutionJournal openedOriginal = ExecutionJournal.Open(
            originalExecutionId,
            plan,
            SqlitePersistenceTestContext.Now.AddMinutes(4));
        Assert.True(
            (await context.JournalStore.CreateAsync(openedOriginal, consumed)).IsSuccess);
        ExecutionJournal originalJournal = await AppendStandardVerifiedAsync(
            context,
            openedOriginal,
            plan.Fingerprint,
            firstMinute: 5);
        VerifiedEntryEvidence directory = DirectoryEvidence();
        VerifiedStepEvidence originalEvidence = new(
            1,
            FilePrimitive.EnsureDirectory,
            sourceRelativePath: null,
            "sorted",
            destinationWasAbsent: true,
            directory);
        var receiptResult = ExecutionReceipt.CreateVerified(
            new ReceiptId(Guid.Parse("77777777-7777-4777-8777-777777777777")),
            plan,
            originalJournal,
            SqlitePersistenceTestContext.Now.AddMinutes(9),
            SqlitePersistenceTestContext.Now.AddHours(2),
            [originalEvidence]);
        Assert.True(receiptResult.IsSuccess, receiptResult.Error?.Code);
        ExecutionReceipt originalReceipt = receiptResult.Value!;
        Assert.True(
            (await context.JournalStore.StoreReceiptAsync(originalReceipt)).IsSuccess);

        RecoveryPlan recoveryPlan = context.CreateRecoveryPlan(
            Guid.Parse("88888888-8888-4888-8888-888888888888"),
            originalJournal,
            directory,
            SqlitePersistenceTestContext.Now.AddMinutes(10));
        StateWriteResult storedRecoveryPlan =
            await context.StateStore.StoreRecoveryPlanAsync(
                recoveryPlan,
                SqlitePersistenceTestContext.CanonicalJson(recoveryPlan));
        Assert.True(storedRecoveryPlan.IsSuccess, storedRecoveryPlan.FailureCode);
        PlanApproval activeUndo = PlanApproval.IssueUndo(
            new ApprovalId(Guid.Parse("99999999-9999-4999-8999-999999999999")),
            recoveryPlan,
            SqlitePersistenceTestContext.Now.AddMinutes(11),
            SqlitePersistenceTestContext.Now.AddMinutes(40));
        await AssertApprovalStoredAsync(context, activeUndo);
        PlanApproval consumedUndo = activeUndo.ConsumeUndo(
            recoveryPlan,
            SqlitePersistenceTestContext.Now.AddMinutes(12)).Value!;
        ExecutionId recoveryExecutionId = new(
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
        ExecutionJournal recoveryJournal = ExecutionJournal.OpenRecovery(
            recoveryExecutionId,
            recoveryPlan,
            SqlitePersistenceTestContext.Now.AddMinutes(12));
        Assert.True(
            (await context.JournalStore.CreateAsync(recoveryJournal, consumedUndo)).IsSuccess);
        recoveryJournal = await AppendRecoveryVerifiedAsync(
            context,
            recoveryJournal,
            recoveryPlan.Fingerprint,
            firstMinute: 13);
        StepRolledBackEvent rollbackLink = new(
            originalJournal.ExecutionId,
            originalJournal.Events.Count + 1L,
            SqlitePersistenceTestContext.Now.AddMinutes(17),
            1,
            recoveryJournal.ExecutionId);
        originalJournal = await AppendAsync(context, originalJournal, rollbackLink);
        VerifiedRecoveryStepEvidence recoveryEvidence = new(
            1,
            1,
            RecoveryPrimitive.RemoveCreatedEntry,
            "sorted",
            destinationRelativePath: null,
            directory);
        var recoveryReceiptResult = RecoveryExecutionReceipt.CreateVerified(
            new ReceiptId(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")),
            recoveryPlan,
            recoveryJournal,
            originalJournal,
            SqlitePersistenceTestContext.Now.AddMinutes(18),
            [recoveryEvidence]);
        Assert.True(recoveryReceiptResult.IsSuccess, recoveryReceiptResult.Error?.Code);
        RecoveryExecutionReceipt recoveryReceipt = recoveryReceiptResult.Value!;
        Assert.True(
            (await context.JournalStore.StoreRecoveryReceiptAsync(recoveryReceipt))
            .IsSuccess);

        Assert.True(
            (await context.JournalStore.CreateAsync(openedOriginal, consumed)).IsSuccess);
        Assert.True(
            (await context.JournalStore.StoreReceiptAsync(originalReceipt)).IsSuccess);
        await context.RestartAsync();

        ExecutionJournalReadResult loadedOriginal =
            await context.JournalStore.LoadJournalAsync(originalExecutionId);
        ExecutionReceiptReadResult loadedOriginalReceipt =
            await context.JournalStore.LoadReceiptAsync(originalExecutionId);
        ExecutionReceiptReadResult loadedRecoveryReceipt =
            await context.JournalStore.LoadReceiptAsync(recoveryExecutionId);
        ExecutionRecoveryScanResult scan =
            await context.JournalStore.ScanRecoveryRequiredAsync();

        Assert.True(loadedOriginal.IsSuccess, loadedOriginal.ReasonCode);
        Assert.Equal(
            StepRecoveryStatus.RolledBack,
            loadedOriginal.Journal!.AssessStep(1).Status);
        Assert.True(loadedOriginalReceipt.IsSuccess, loadedOriginalReceipt.ReasonCode);
        Assert.Equal(PersistedReceiptKind.Standard, loadedOriginalReceipt.Kind);
        Assert.Equal(originalReceipt.Id, loadedOriginalReceipt.StandardReceipt!.Id);
        Assert.True(loadedRecoveryReceipt.IsSuccess, loadedRecoveryReceipt.ReasonCode);
        Assert.Equal(PersistedReceiptKind.Recovery, loadedRecoveryReceipt.Kind);
        Assert.Equal(
            originalExecutionId,
            loadedRecoveryReceipt.RecoveryReceipt!.OriginalExecutionId);
        Assert.True(scan.IsSuccess, scan.ReasonCode);
        Assert.Empty(scan.Candidates);
    }

    [Fact]
    public async Task ApprovalConsumptionIsAtomicAndCrashScanOnlyReportsInspection()
    {
        using SqlitePersistenceTestContext context =
            await SqlitePersistenceTestContext.CreateAsync();
        await context.SeedAuthorityAndSkillAsync();
        ExecutionPlan plan = context.CreateStandardPlan(
            Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"));
        Assert.True(
            (await context.StateStore.StoreExecutionPlanAsync(
                plan,
                SqlitePersistenceTestContext.CanonicalJson(plan))).IsSuccess);
        PlanApproval active = PlanApproval.Issue(
            new ApprovalId(Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd")),
            plan,
            SqlitePersistenceTestContext.Now.AddMinutes(3),
            SqlitePersistenceTestContext.Now.AddMinutes(30));
        await AssertApprovalStoredAsync(context, active);
        PlanApproval consumed = active.Consume(
            plan,
            SqlitePersistenceTestContext.Now.AddMinutes(4)).Value!;
        ExecutionJournal first = ExecutionJournal.Open(
            new ExecutionId(Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee")),
            plan,
            SqlitePersistenceTestContext.Now.AddMinutes(4));
        ExecutionJournal second = ExecutionJournal.Open(
            new ExecutionId(Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff")),
            plan,
            SqlitePersistenceTestContext.Now.AddMinutes(4));

        JournalWriteResult[] writes = await Task.WhenAll(
            context.JournalStore.CreateAsync(first, consumed).AsTask(),
            context.JournalStore.CreateAsync(second, consumed).AsTask());

        Assert.Single(writes, static result => result.IsSuccess);
        ExecutionJournal winner = writes[0].IsSuccess ? first : second;
        Assert.True(
            (await context.JournalStore.CreateAsync(winner, consumed)).IsSuccess);
        winner = await AppendAsync(
            context,
            winner,
            new StepIntentRecordedEvent(
                winner.ExecutionId,
                2,
                SqlitePersistenceTestContext.Now.AddMinutes(5),
                1,
                FilePrimitive.EnsureDirectory,
                plan.Fingerprint,
                JournalInverseKind.RemoveCreatedEntry));
        winner = await AppendAsync(
            context,
            winner,
            new StepMutationObservedEvent(
                winner.ExecutionId,
                3,
                SqlitePersistenceTestContext.Now.AddMinutes(6),
                1));
        int eventCount = winner.Events.Count;
        await context.RestartAsync();

        ExecutionRecoveryScanResult scan =
            await context.JournalStore.ScanRecoveryRequiredAsync();
        ExecutionJournalReadResult loaded =
            await context.JournalStore.LoadJournalAsync(winner.ExecutionId);

        Assert.True(scan.IsSuccess, scan.ReasonCode);
        ExecutionRecoveryCandidate candidate = Assert.Single(scan.Candidates);
        Assert.Equal(winner.ExecutionId, candidate.ExecutionId);
        Assert.Equal(
            "persistence.recovery_inspection_required",
            candidate.ReasonCode);
        StepRecoveryAssessment assessment = Assert.Single(candidate.Steps);
        Assert.Equal(StepRecoveryStatus.StartedUncommitted, assessment.Status);
        Assert.True(assessment.RequiresFileSystemInspection);
        Assert.True(loaded.IsSuccess, loaded.ReasonCode);
        Assert.Equal(eventCount, loaded.Journal!.Events.Count);
    }

    [Fact]
    public async Task TamperedJournalEventIdentityAndReceiptShapeFailClosed()
    {
        using SqlitePersistenceTestContext journalContext =
            await SqlitePersistenceTestContext.CreateAsync();
        await journalContext.SeedAuthorityAndSkillAsync();
        ExecutionPlan journalPlan = journalContext.CreateStandardPlan(
            Guid.Parse("01234567-89ab-4cde-8fab-0123456789ab"));
        ExecutionJournal journal = await OpenExecutionAsync(
            journalContext,
            journalPlan,
            Guid.Parse("12345678-9abc-4def-8abc-123456789abc"),
            Guid.Parse("23456789-abcd-4efa-8bcd-23456789abcd"));
        journal = await AppendAsync(
            journalContext,
            journal,
            new StepIntentRecordedEvent(
                journal.ExecutionId,
                2,
                SqlitePersistenceTestContext.Now.AddMinutes(5),
                1,
                FilePrimitive.EnsureDirectory,
                journalPlan.Fingerprint,
                JournalInverseKind.RemoveCreatedEntry));
        await using (SqliteConnection connection = await journalContext.OpenRawAsync())
        {
            await ExecuteAsync(
                connection,
                "DROP TRIGGER execution_journal_events_no_update; " +
                "UPDATE execution_journal_events SET journal_event_id = 'tampered:2' " +
                "WHERE event_sequence = 2; " +
                "CREATE TRIGGER execution_journal_events_no_update " +
                "BEFORE UPDATE ON execution_journal_events BEGIN " +
                "SELECT RAISE(ABORT, 'execution_journal_is_append_only'); END;");
        }

        ExecutionJournalReadResult corruptedJournal =
            await journalContext.JournalStore.LoadJournalAsync(journal.ExecutionId);
        Assert.False(corruptedJournal.IsSuccess);
        Assert.Equal("persistence.journal_corrupt", corruptedJournal.ReasonCode);

        using SqlitePersistenceTestContext receiptContext =
            await SqlitePersistenceTestContext.CreateAsync();
        await receiptContext.SeedAuthorityAndSkillAsync();
        ExecutionPlan receiptPlan = receiptContext.CreateStandardPlan(
            Guid.Parse("3456789a-bcde-4fab-8cde-3456789abcde"));
        ExecutionJournal receiptJournal = await OpenExecutionAsync(
            receiptContext,
            receiptPlan,
            Guid.Parse("456789ab-cdef-4abc-8def-456789abcdef"),
            Guid.Parse("56789abc-def0-4bcd-8ef0-56789abcdef0"));
        receiptJournal = await AppendStandardVerifiedAsync(
            receiptContext,
            receiptJournal,
            receiptPlan.Fingerprint,
            firstMinute: 5);
        VerifiedEntryEvidence directory = DirectoryEvidence();
        ExecutionReceipt receipt = ExecutionReceipt.CreateVerified(
            new ReceiptId(Guid.Parse("6789abcd-ef01-4cde-8f01-6789abcdef01")),
            receiptPlan,
            receiptJournal,
            SqlitePersistenceTestContext.Now.AddMinutes(9),
            undoAvailableUntilUtc: null,
            [
                new VerifiedStepEvidence(
                    1,
                    FilePrimitive.EnsureDirectory,
                    sourceRelativePath: null,
                    "sorted",
                    destinationWasAbsent: true,
                    directory),
            ]).Value!;
        Assert.True(
            (await receiptContext.JournalStore.StoreReceiptAsync(receipt)).IsSuccess);
        await using (SqliteConnection connection = await receiptContext.OpenRawAsync())
        {
            await ExecuteAsync(
                connection,
                "DROP TRIGGER receipts_no_update; " +
                "UPDATE receipts SET receipt_json = " +
                "json_set(receipt_json, '$.unexpected', 1); " +
                "CREATE TRIGGER receipts_no_update BEFORE UPDATE ON receipts BEGIN " +
                "SELECT RAISE(ABORT, 'receipts_are_immutable'); END;");
        }

        ExecutionReceiptReadResult corruptedReceipt =
            await receiptContext.JournalStore.LoadReceiptAsync(receipt.ExecutionId);
        Assert.False(corruptedReceipt.IsSuccess);
        Assert.Equal("persistence.receipt_corrupt", corruptedReceipt.ReasonCode);
    }

    private static async Task<ExecutionJournal> OpenExecutionAsync(
        SqlitePersistenceTestContext context,
        ExecutionPlan plan,
        Guid approvalId,
        Guid executionId)
    {
        StateWriteResult storedPlan = await context.StateStore.StoreExecutionPlanAsync(
            plan,
            SqlitePersistenceTestContext.CanonicalJson(plan));
        Assert.True(storedPlan.IsSuccess, storedPlan.FailureCode);
        PlanApproval active = PlanApproval.Issue(
            new ApprovalId(approvalId),
            plan,
            SqlitePersistenceTestContext.Now.AddMinutes(3),
            SqlitePersistenceTestContext.Now.AddMinutes(30));
        await AssertApprovalStoredAsync(context, active);
        PlanApproval consumed = active.Consume(
            plan,
            SqlitePersistenceTestContext.Now.AddMinutes(4)).Value!;
        ExecutionJournal journal = ExecutionJournal.Open(
            new ExecutionId(executionId),
            plan,
            SqlitePersistenceTestContext.Now.AddMinutes(4));
        JournalWriteResult opened = await context.JournalStore.CreateAsync(
            journal,
            consumed);
        Assert.True(opened.IsSuccess, opened.FailureCode);
        return journal;
    }

    private static async Task<ExecutionJournal> AppendStandardVerifiedAsync(
        SqlitePersistenceTestContext context,
        ExecutionJournal journal,
        PlanFingerprint fingerprint,
        int firstMinute)
    {
        journal = await AppendAsync(
            context,
            journal,
            new StepIntentRecordedEvent(
                journal.ExecutionId,
                journal.Events.Count + 1L,
                SqlitePersistenceTestContext.Now.AddMinutes(firstMinute),
                1,
                FilePrimitive.EnsureDirectory,
                fingerprint,
                JournalInverseKind.RemoveCreatedEntry));
        journal = await AppendAsync(
            context,
            journal,
            new StepMutationObservedEvent(
                journal.ExecutionId,
                journal.Events.Count + 1L,
                SqlitePersistenceTestContext.Now.AddMinutes(firstMinute + 1),
                1));
        journal = await AppendAsync(
            context,
            journal,
            new StepCommittedEvent(
                journal.ExecutionId,
                journal.Events.Count + 1L,
                SqlitePersistenceTestContext.Now.AddMinutes(firstMinute + 2),
                1));
        return await AppendAsync(
            context,
            journal,
            new StepVerifiedEvent(
                journal.ExecutionId,
                journal.Events.Count + 1L,
                SqlitePersistenceTestContext.Now.AddMinutes(firstMinute + 3),
                1));
    }

    private static async Task<ExecutionJournal> AppendRecoveryVerifiedAsync(
        SqlitePersistenceTestContext context,
        ExecutionJournal journal,
        PlanFingerprint fingerprint,
        int firstMinute)
    {
        journal = await AppendAsync(
            context,
            journal,
            new RecoveryStepIntentRecordedEvent(
                journal.ExecutionId,
                journal.Events.Count + 1L,
                SqlitePersistenceTestContext.Now.AddMinutes(firstMinute),
                1,
                RecoveryPrimitive.RemoveCreatedEntry,
                1,
                fingerprint));
        journal = await AppendAsync(
            context,
            journal,
            new StepMutationObservedEvent(
                journal.ExecutionId,
                journal.Events.Count + 1L,
                SqlitePersistenceTestContext.Now.AddMinutes(firstMinute + 1),
                1));
        journal = await AppendAsync(
            context,
            journal,
            new StepCommittedEvent(
                journal.ExecutionId,
                journal.Events.Count + 1L,
                SqlitePersistenceTestContext.Now.AddMinutes(firstMinute + 2),
                1));
        return await AppendAsync(
            context,
            journal,
            new StepVerifiedEvent(
                journal.ExecutionId,
                journal.Events.Count + 1L,
                SqlitePersistenceTestContext.Now.AddMinutes(firstMinute + 3),
                1));
    }

    private static async Task<ExecutionJournal> AppendAsync(
        SqlitePersistenceTestContext context,
        ExecutionJournal journal,
        ExecutionJournalEvent journalEvent)
    {
        var appended = journal.Append(journalEvent);
        Assert.True(appended.IsSuccess, appended.Error?.Code);
        JournalWriteResult persisted = await context.JournalStore.AppendAsync(journalEvent);
        Assert.True(persisted.IsSuccess, persisted.FailureCode);
        return appended.Value!;
    }

    private static async Task AssertApprovalStoredAsync(
        SqlitePersistenceTestContext context,
        PlanApproval approval)
    {
        StateWriteResult stored = await context.StateStore.StoreApprovalAsync(approval);
        Assert.True(stored.IsSuccess, stored.FailureCode);
    }

    private static VerifiedEntryEvidence DirectoryEvidence() =>
        new(
            VerifiedEntryKind.Directory,
            "test-volume",
            "test-directory-entry",
            length: null,
            SqlitePersistenceTestContext.Now,
            SqlitePersistenceTestContext.Now.AddMinutes(1),
            attributes: 16,
            contentHash: null);

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync();
    }
}
