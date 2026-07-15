using Tooltail.Domain.Identifiers;

namespace Tooltail.Domain.Execution;

public enum JournalInverseKind
{
    None,
    RenameBack,
    MoveBack,
    RemoveCreatedEntry,
}

public abstract record ExecutionJournalEvent
{
    private protected ExecutionJournalEvent(
        ExecutionId executionId,
        long eventSequence,
        DateTimeOffset occurredUtc,
        int? stepSequence)
    {
        IdentifierGuard.NotEmpty(executionId.Value);
        ArgumentOutOfRangeException.ThrowIfLessThan(eventSequence, 1);
        UtcGuard.RequireUtc(occurredUtc, nameof(occurredUtc));
        if (stepSequence is not null)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(stepSequence.Value, 1);
        }

        ExecutionId = executionId;
        EventSequence = eventSequence;
        OccurredUtc = occurredUtc;
        StepSequence = stepSequence;
    }

    public ExecutionId ExecutionId { get; }

    public long EventSequence { get; }

    public DateTimeOffset OccurredUtc { get; }

    public int? StepSequence { get; }
}

public sealed record ExecutionOpenedEvent : ExecutionJournalEvent
{
    public ExecutionOpenedEvent(
        ExecutionId executionId,
        DateTimeOffset occurredUtc,
        PlanId planId,
        PlanFingerprint planFingerprint)
        : base(executionId, 1, occurredUtc, stepSequence: null)
    {
        IdentifierGuard.NotEmpty(planId.Value);
        ArgumentNullException.ThrowIfNull(planFingerprint);
        PlanId = planId;
        PlanFingerprint = planFingerprint;
    }

    public PlanId PlanId { get; }

    public PlanFingerprint PlanFingerprint { get; }
}

public sealed record StepIntentRecordedEvent : ExecutionJournalEvent
{
    public StepIntentRecordedEvent(
        ExecutionId executionId,
        long eventSequence,
        DateTimeOffset occurredUtc,
        int stepSequence,
        FilePrimitive primitive,
        PlanFingerprint preconditionFingerprint,
        JournalInverseKind inverseKind)
        : base(executionId, eventSequence, occurredUtc, stepSequence)
    {
        if (!Enum.IsDefined(primitive))
        {
            throw new ArgumentOutOfRangeException(nameof(primitive));
        }

        if (!Enum.IsDefined(inverseKind))
        {
            throw new ArgumentOutOfRangeException(nameof(inverseKind));
        }

        ArgumentNullException.ThrowIfNull(preconditionFingerprint);
        Primitive = primitive;
        PreconditionFingerprint = preconditionFingerprint;
        InverseKind = inverseKind;
    }

    public FilePrimitive Primitive { get; }

    public PlanFingerprint PreconditionFingerprint { get; }

    public JournalInverseKind InverseKind { get; }
}

public sealed record RecoveryStepIntentRecordedEvent : ExecutionJournalEvent
{
    public RecoveryStepIntentRecordedEvent(
        ExecutionId executionId,
        long eventSequence,
        DateTimeOffset occurredUtc,
        int stepSequence,
        RecoveryPrimitive primitive,
        int originalStepSequence,
        PlanFingerprint preconditionFingerprint)
        : base(executionId, eventSequence, occurredUtc, stepSequence)
    {
        if (!Enum.IsDefined(primitive))
        {
            throw new ArgumentOutOfRangeException(nameof(primitive));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(originalStepSequence, 1);
        ArgumentNullException.ThrowIfNull(preconditionFingerprint);
        Primitive = primitive;
        OriginalStepSequence = originalStepSequence;
        PreconditionFingerprint = preconditionFingerprint;
    }

    public RecoveryPrimitive Primitive { get; }

    public int OriginalStepSequence { get; }

    public PlanFingerprint PreconditionFingerprint { get; }
}

public sealed record StepMutationObservedEvent : ExecutionJournalEvent
{
    public StepMutationObservedEvent(
        ExecutionId executionId,
        long eventSequence,
        DateTimeOffset occurredUtc,
        int stepSequence)
        : base(executionId, eventSequence, occurredUtc, stepSequence)
    {
    }
}

public sealed record StepCommittedEvent : ExecutionJournalEvent
{
    public StepCommittedEvent(
        ExecutionId executionId,
        long eventSequence,
        DateTimeOffset occurredUtc,
        int stepSequence)
        : base(executionId, eventSequence, occurredUtc, stepSequence)
    {
    }
}

public sealed record StepVerifiedEvent : ExecutionJournalEvent
{
    public StepVerifiedEvent(
        ExecutionId executionId,
        long eventSequence,
        DateTimeOffset occurredUtc,
        int stepSequence)
        : base(executionId, eventSequence, occurredUtc, stepSequence)
    {
    }
}

public sealed record StepFailedEvent : ExecutionJournalEvent
{
    public StepFailedEvent(
        ExecutionId executionId,
        long eventSequence,
        DateTimeOffset occurredUtc,
        int stepSequence,
        string failureCode)
        : base(executionId, eventSequence, occurredUtc, stepSequence)
    {
        ReasonCodeGuard.Validate(failureCode, nameof(failureCode));
        FailureCode = failureCode;
    }

    public string FailureCode { get; }
}

public sealed record StepRecoveryRequiredEvent : ExecutionJournalEvent
{
    public StepRecoveryRequiredEvent(
        ExecutionId executionId,
        long eventSequence,
        DateTimeOffset occurredUtc,
        int stepSequence,
        string reasonCode)
        : base(executionId, eventSequence, occurredUtc, stepSequence)
    {
        ReasonCodeGuard.Validate(reasonCode, nameof(reasonCode));
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

public sealed record StepRolledBackEvent : ExecutionJournalEvent
{
    public StepRolledBackEvent(
        ExecutionId executionId,
        long eventSequence,
        DateTimeOffset occurredUtc,
        int stepSequence,
        ExecutionId recoveryExecutionId)
        : base(executionId, eventSequence, occurredUtc, stepSequence)
    {
        IdentifierGuard.NotEmpty(recoveryExecutionId.Value);
        if (executionId == recoveryExecutionId)
        {
            throw new ArgumentException(
                "Rollback must be performed by a separately authorized recovery execution.",
                nameof(recoveryExecutionId));
        }

        RecoveryExecutionId = recoveryExecutionId;
    }

    public ExecutionId RecoveryExecutionId { get; }
}

internal static class ReasonCodeGuard
{
    public static void Validate(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 128 || value.Any(static character => !IsAllowed(character)))
        {
            throw new ArgumentException(
                "Reason codes must use a bounded diagnostic-safe alphabet.",
                parameterName);
        }
    }

    private static bool IsAllowed(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-';
}
