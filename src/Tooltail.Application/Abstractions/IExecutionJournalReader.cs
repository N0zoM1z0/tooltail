using System.Collections.ObjectModel;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Application.Abstractions;

public enum PersistedReceiptKind
{
    Standard,
    Recovery,
}

public sealed record ExecutionJournalReadResult(
    bool IsSuccess,
    string ReasonCode,
    ExecutionJournal? Journal)
{
    public static ExecutionJournalReadResult Success(ExecutionJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return new(true, "persistence.journal_read", journal);
    }

    public static ExecutionJournalReadResult Failure(string reasonCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        return new(false, reasonCode, null);
    }
}

public sealed record ExecutionReceiptReadResult(
    bool IsSuccess,
    string ReasonCode,
    PersistedReceiptKind? Kind,
    ExecutionReceipt? StandardReceipt,
    RecoveryExecutionReceipt? RecoveryReceipt)
{
    public static ExecutionReceiptReadResult Standard(ExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return new(
            true,
            "persistence.receipt_read",
            PersistedReceiptKind.Standard,
            receipt,
            null);
    }

    public static ExecutionReceiptReadResult Recovery(RecoveryExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return new(
            true,
            "persistence.receipt_read",
            PersistedReceiptKind.Recovery,
            null,
            receipt);
    }

    public static ExecutionReceiptReadResult Failure(string reasonCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        return new(false, reasonCode, null, null, null);
    }
}

public sealed record ExecutionRecoveryCandidate
{
    public ExecutionRecoveryCandidate(
        ExecutionId executionId,
        ExecutionJournalKind journalKind,
        IEnumerable<StepRecoveryAssessment> steps,
        string reasonCode)
    {
        if (executionId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "A recovery candidate requires a non-empty execution identity.",
                nameof(executionId));
        }

        if (!Enum.IsDefined(journalKind))
        {
            throw new ArgumentOutOfRangeException(nameof(journalKind));
        }

        ArgumentNullException.ThrowIfNull(steps);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ExecutionId = executionId;
        JournalKind = journalKind;
        StepRecoveryAssessment[] materialized = steps.Take(10_001).ToArray();
        if (materialized.Length > 10_000 || materialized.Any(static step => step is null))
        {
            throw new ArgumentException(
                "A recovery candidate exceeds its bounded step shape.",
                nameof(steps));
        }

        Steps = new ReadOnlyCollection<StepRecoveryAssessment>(materialized);
        ReasonCode = reasonCode;
    }

    public ExecutionId ExecutionId { get; }

    public ExecutionJournalKind JournalKind { get; }

    public IReadOnlyList<StepRecoveryAssessment> Steps { get; }

    public string ReasonCode { get; }
}

public sealed record ExecutionRecoveryScanResult(
    bool IsSuccess,
    string ReasonCode,
    IReadOnlyList<ExecutionRecoveryCandidate> Candidates)
{
    public static ExecutionRecoveryScanResult Success(
        IEnumerable<ExecutionRecoveryCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ExecutionRecoveryCandidate[] materialized = candidates.Take(1_001).ToArray();
        if (materialized.Length > 1_000 ||
            materialized.Any(static candidate => candidate is null))
        {
            throw new ArgumentException(
                "A recovery scan exceeds its bounded candidate shape.",
                nameof(candidates));
        }

        return new(
            true,
            "persistence.recovery_scan_complete",
            new ReadOnlyCollection<ExecutionRecoveryCandidate>(materialized));
    }

    public static ExecutionRecoveryScanResult Failure(string reasonCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        return new(false, reasonCode, []);
    }
}

public interface IExecutionJournalReader
{
    ValueTask<ExecutionJournalReadResult> LoadJournalAsync(
        ExecutionId executionId,
        CancellationToken cancellationToken = default);

    ValueTask<ExecutionReceiptReadResult> LoadReceiptAsync(
        ExecutionId executionId,
        CancellationToken cancellationToken = default);

    ValueTask<ExecutionRecoveryScanResult> ScanRecoveryRequiredAsync(
        CancellationToken cancellationToken = default);
}
