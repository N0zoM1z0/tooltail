using Tooltail.Application.Abstractions;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;

namespace Tooltail.SkillFixtureCli;

internal sealed class InMemoryExecutionJournalStore : IExecutionJournalStore
{
    private readonly Dictionary<ExecutionId, ExecutionJournal> journals = [];
    private readonly Dictionary<ExecutionId, object> receipts = [];

    public ValueTask<JournalWriteResult> CreateAsync(
        ExecutionJournal journal,
        PlanApproval consumedApproval,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (journals.TryGetValue(journal.ExecutionId, out ExecutionJournal? existing))
        {
            return ValueTask.FromResult(existing == journal
                ? JournalWriteResult.Success
                : JournalWriteResult.Failure("fixture.journal_conflict"));
        }

        if (consumedApproval.State != PlanApprovalState.Consumed ||
            consumedApproval.PlanId != journal.PlanId ||
            consumedApproval.Fingerprint != journal.PlanFingerprint)
        {
            return ValueTask.FromResult(
                JournalWriteResult.Failure("fixture.approval_invalid"));
        }

        journals.Add(journal.ExecutionId, journal);
        return ValueTask.FromResult(JournalWriteResult.Success);
    }

    public ValueTask<JournalWriteResult> AppendAsync(
        ExecutionJournalEvent journalEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!journals.TryGetValue(journalEvent.ExecutionId, out ExecutionJournal? journal))
        {
            return ValueTask.FromResult(
                JournalWriteResult.Failure("fixture.journal_missing"));
        }

        var appended = journal.Append(journalEvent);
        if (!appended.IsSuccess)
        {
            return ValueTask.FromResult(
                JournalWriteResult.Failure(appended.Error!.Code));
        }

        journals[journalEvent.ExecutionId] = appended.Value!;
        return ValueTask.FromResult(JournalWriteResult.Success);
    }

    public ValueTask<JournalWriteResult> StoreReceiptAsync(
        ExecutionReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(StoreReceipt(receipt.ExecutionId, receipt));
    }

    public ValueTask<JournalWriteResult> StoreRecoveryReceiptAsync(
        RecoveryExecutionReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(StoreReceipt(receipt.ExecutionId, receipt));
    }

    private JournalWriteResult StoreReceipt(ExecutionId executionId, object receipt)
    {
        if (!journals.ContainsKey(executionId))
        {
            return JournalWriteResult.Failure("fixture.journal_missing");
        }

        if (receipts.TryGetValue(executionId, out object? existing))
        {
            return Equals(existing, receipt)
                ? JournalWriteResult.Success
                : JournalWriteResult.Failure("fixture.receipt_conflict");
        }

        receipts.Add(executionId, receipt);
        return JournalWriteResult.Success;
    }
}
