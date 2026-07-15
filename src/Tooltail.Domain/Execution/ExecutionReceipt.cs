using System.Collections.ObjectModel;
using Tooltail.Domain.Common;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Domain.Execution;

public sealed record ExecutionReceipt
{
    private ExecutionReceipt(
        ReceiptId id,
        ExecutionId executionId,
        PlanId planId,
        PlanFingerprint planFingerprint,
        DateTimeOffset completedUtc,
        int verifiedStepCount,
        DateTimeOffset? undoAvailableUntilUtc,
        IEnumerable<string> residualEffectCodes)
    {
        Id = id;
        ExecutionId = executionId;
        PlanId = planId;
        PlanFingerprint = planFingerprint;
        CompletedUtc = completedUtc;
        VerifiedStepCount = verifiedStepCount;
        UndoAvailableUntilUtc = undoAvailableUntilUtc;
        ResidualEffectCodes = new ReadOnlyCollection<string>(residualEffectCodes.ToArray());
    }

    public ReceiptId Id { get; }

    public ExecutionId ExecutionId { get; }

    public PlanId PlanId { get; }

    public PlanFingerprint PlanFingerprint { get; }

    public DateTimeOffset CompletedUtc { get; }

    public int VerifiedStepCount { get; }

    public DateTimeOffset? UndoAvailableUntilUtc { get; }

    public IReadOnlyList<string> ResidualEffectCodes { get; }

    public static DomainResult<ExecutionReceipt> CreateVerified(
        ReceiptId id,
        ExecutionJournal journal,
        DateTimeOffset completedUtc,
        DateTimeOffset? undoAvailableUntilUtc)
    {
        IdentifierGuard.NotEmpty(id.Value);
        ArgumentNullException.ThrowIfNull(journal);
        UtcGuard.RequireUtc(completedUtc, nameof(completedUtc));
        if (completedUtc < journal.Events[^1].OccurredUtc)
        {
            return DomainResult.Failure<ExecutionReceipt>(
                "receipt.completion_before_journal",
                "A receipt cannot complete before its journal.");
        }

        if (undoAvailableUntilUtc is not null)
        {
            UtcGuard.RequireUtc(undoAvailableUntilUtc.Value, nameof(undoAvailableUntilUtc));
            if (undoAvailableUntilUtc <= completedUtc)
            {
                return DomainResult.Failure<ExecutionReceipt>(
                    "receipt.undo_window_invalid",
                    "An undo window must end after receipt completion.");
            }
        }

        for (int step = 1; step <= journal.OperationPrimitives.Count; step++)
        {
            if (journal.AssessStep(step).Status != StepRecoveryStatus.Verified)
            {
                return DomainResult.Failure<ExecutionReceipt>(
                    "receipt.execution_not_verified",
                    "A successful receipt requires every planned step to be verified.");
            }
        }

        return DomainResult.Success(
            new ExecutionReceipt(
                id,
                journal.ExecutionId,
                journal.PlanId,
                journal.PlanFingerprint,
                completedUtc,
                journal.OperationPrimitives.Count,
                undoAvailableUntilUtc,
                []));
    }
}
