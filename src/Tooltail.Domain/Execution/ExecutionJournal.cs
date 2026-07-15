using System.Collections.ObjectModel;
using Tooltail.Domain.Common;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Domain.Execution;

public enum ExecutionJournalKind
{
    Standard,
    Recovery,
}

public enum StepRecoveryStatus
{
    NotStarted,
    StartedUncommitted,
    CommittedUnverified,
    Verified,
    RecoveryRequired,
    RolledBack,
}

public sealed record StepRecoveryAssessment(
    StepRecoveryStatus Status,
    bool RequiresFileSystemInspection,
    string? FailureCode,
    string? RecoveryReasonCode);

public sealed record ExecutionJournal
{
    private ExecutionJournal(
        ExecutionId executionId,
        PlanId planId,
        PlanFingerprint planFingerprint,
        ExecutionJournalKind kind,
        IEnumerable<FilePrimitive> operationPrimitives,
        IEnumerable<JournalInverseKind> operationInverseKinds,
        IEnumerable<RecoveryPrimitive> recoveryOperationPrimitives,
        IEnumerable<int> recoveryOriginalStepSequences,
        IEnumerable<ExecutionJournalEvent> events)
    {
        ExecutionId = executionId;
        PlanId = planId;
        PlanFingerprint = planFingerprint;
        Kind = kind;
        OperationPrimitives = new ReadOnlyCollection<FilePrimitive>(operationPrimitives.ToArray());
        OperationInverseKinds = new ReadOnlyCollection<JournalInverseKind>(operationInverseKinds.ToArray());
        RecoveryOperationPrimitives = new ReadOnlyCollection<RecoveryPrimitive>(
            recoveryOperationPrimitives.ToArray());
        RecoveryOriginalStepSequences = new ReadOnlyCollection<int>(
            recoveryOriginalStepSequences.ToArray());
        Events = new ReadOnlyCollection<ExecutionJournalEvent>(events.ToArray());
    }

    public ExecutionId ExecutionId { get; }

    public PlanId PlanId { get; }

    public PlanFingerprint PlanFingerprint { get; }

    public ExecutionJournalKind Kind { get; }

    public IReadOnlyList<FilePrimitive> OperationPrimitives { get; }

    public IReadOnlyList<JournalInverseKind> OperationInverseKinds { get; }

    public IReadOnlyList<RecoveryPrimitive> RecoveryOperationPrimitives { get; }

    public IReadOnlyList<int> RecoveryOriginalStepSequences { get; }

    public int OperationCount => Kind == ExecutionJournalKind.Standard
        ? OperationPrimitives.Count
        : RecoveryOperationPrimitives.Count;

    public IReadOnlyList<ExecutionJournalEvent> Events { get; }

    public static ExecutionJournal Open(
        ExecutionId executionId,
        ExecutionPlan plan,
        DateTimeOffset openedUtc)
    {
        IdentifierGuard.NotEmpty(executionId.Value);
        ArgumentNullException.ThrowIfNull(plan);
        UtcGuard.RequireUtc(openedUtc, nameof(openedUtc));
        if (openedUtc < plan.Definition.CreatedUtc || openedUtc >= plan.Definition.ExpiresUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(openedUtc));
        }

        FilePrimitive[] primitives = plan.Definition.Operations
            .Select(static operation => operation.Primitive)
            .ToArray();
        JournalInverseKind[] inverseKinds = plan.Definition.Operations
            .Select(ExpectedInverseKind)
            .ToArray();
        ExecutionOpenedEvent opened = new(
            executionId,
            openedUtc,
            plan.Definition.Id,
            plan.Fingerprint);
        return new ExecutionJournal(
            executionId,
            plan.Definition.Id,
            plan.Fingerprint,
            ExecutionJournalKind.Standard,
            primitives,
            inverseKinds,
            [],
            [],
            [opened]);
    }

    public static ExecutionJournal OpenRecovery(
        ExecutionId executionId,
        RecoveryPlan plan,
        DateTimeOffset openedUtc)
    {
        IdentifierGuard.NotEmpty(executionId.Value);
        ArgumentNullException.ThrowIfNull(plan);
        UtcGuard.RequireUtc(openedUtc, nameof(openedUtc));
        if (executionId == plan.Definition.OriginalExecutionId)
        {
            throw new ArgumentException(
                "Recovery must use a distinct execution identity.",
                nameof(executionId));
        }

        if (openedUtc < plan.Definition.CreatedUtc || openedUtc >= plan.Definition.ExpiresUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(openedUtc));
        }

        RecoveryPrimitive[] primitives = plan.Definition.Operations
            .Select(static operation => operation.Primitive)
            .ToArray();
        int[] originalSteps = plan.Definition.Operations
            .Select(static operation => operation.OriginalStepSequence)
            .ToArray();
        ExecutionOpenedEvent opened = new(
            executionId,
            openedUtc,
            plan.Definition.Id,
            plan.Fingerprint);
        return new ExecutionJournal(
            executionId,
            plan.Definition.Id,
            plan.Fingerprint,
            ExecutionJournalKind.Recovery,
            [],
            [],
            primitives,
            originalSteps,
            [opened]);
    }

    public DomainResult<ExecutionJournal> Append(ExecutionJournalEvent journalEvent)
    {
        ArgumentNullException.ThrowIfNull(journalEvent);
        if (journalEvent.ExecutionId != ExecutionId)
        {
            return Failure("journal.execution_mismatch", "The event belongs to a different execution.");
        }

        if (journalEvent.EventSequence != Events.Count + 1L)
        {
            return Failure("journal.event_sequence_invalid", "Journal event sequences must be contiguous.");
        }

        if (journalEvent.OccurredUtc < Events[^1].OccurredUtc)
        {
            return Failure("journal.time_regressed", "Journal event time cannot move backwards.");
        }

        if (journalEvent is ExecutionOpenedEvent)
        {
            return Failure("journal.already_open", "An execution journal can be opened only once.");
        }

        if (journalEvent.StepSequence is null ||
            journalEvent.StepSequence > OperationCount)
        {
            return Failure("journal.step_out_of_range", "The journal event references an unknown plan step.");
        }

        int stepSequence = journalEvent.StepSequence.Value;
        StepFacts facts = GetFacts(stepSequence);
        string? transitionError = ValidateTransition(journalEvent, stepSequence, facts);
        if (transitionError is not null)
        {
            return Failure("journal.transition_invalid", transitionError);
        }

        ExecutionJournalEvent[] appended = [.. Events, journalEvent];
        return DomainResult.Success(
            new ExecutionJournal(
                ExecutionId,
                PlanId,
                PlanFingerprint,
                Kind,
                OperationPrimitives,
                OperationInverseKinds,
                RecoveryOperationPrimitives,
                RecoveryOriginalStepSequences,
                appended));
    }

    public StepRecoveryAssessment AssessStep(int stepSequence)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stepSequence, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(stepSequence, OperationCount);

        StepFacts facts = GetFacts(stepSequence);
        StepRecoveryStatus status = facts.HasRollback
            ? StepRecoveryStatus.RolledBack
            : facts.HasRecoveryRequired
                ? StepRecoveryStatus.RecoveryRequired
                : facts.HasVerified
                    ? StepRecoveryStatus.Verified
                    : facts.HasCommitted
                        ? StepRecoveryStatus.CommittedUnverified
                        : facts.HasIntent
                            ? StepRecoveryStatus.StartedUncommitted
                            : StepRecoveryStatus.NotStarted;
        bool requiresInspection = status is
            StepRecoveryStatus.StartedUncommitted or
            StepRecoveryStatus.CommittedUnverified or
            StepRecoveryStatus.RecoveryRequired;
        return new StepRecoveryAssessment(
            status,
            requiresInspection,
            facts.FailureCode,
            facts.RecoveryReasonCode);
    }

    private string? ValidateTransition(
        ExecutionJournalEvent journalEvent,
        int stepSequence,
        StepFacts facts)
    {
        if (journalEvent is StepIntentRecordedEvent intent)
        {
            if (Kind != ExecutionJournalKind.Standard ||
                facts.HasIntent ||
                !PreviousStepsAreVerified(stepSequence))
            {
                return "A step intent must be the first event for the next fully ordered step.";
            }

            if (intent.Primitive != OperationPrimitives[stepSequence - 1] ||
                intent.PreconditionFingerprint != PlanFingerprint ||
                intent.InverseKind != OperationInverseKinds[stepSequence - 1])
            {
                return "The step intent does not match the authorized plan.";
            }

            return null;
        }

        if (journalEvent is RecoveryStepIntentRecordedEvent recoveryIntent)
        {
            if (Kind != ExecutionJournalKind.Recovery ||
                facts.HasIntent ||
                !PreviousStepsAreVerified(stepSequence))
            {
                return "A recovery intent must be the first event for the next ordered recovery step.";
            }

            if (recoveryIntent.Primitive != RecoveryOperationPrimitives[stepSequence - 1] ||
                recoveryIntent.OriginalStepSequence !=
                    RecoveryOriginalStepSequences[stepSequence - 1] ||
                recoveryIntent.PreconditionFingerprint != PlanFingerprint)
            {
                return "The recovery intent does not match the authorized recovery plan.";
            }

            return null;
        }

        if (journalEvent is StepRolledBackEvent)
        {
            if (Kind == ExecutionJournalKind.Recovery ||
                facts.HasRollback ||
                (!facts.HasVerified && !facts.HasRecoveryRequired))
            {
                return "Only a verified or recovery-required standard step can be linked to a distinct recovery execution.";
            }

            return null;
        }

        if (!facts.HasIntent || facts.HasVerified || facts.HasRollback)
        {
            return "The step is not in a state that accepts this event.";
        }

        return journalEvent switch
        {
            StepMutationObservedEvent when
                !facts.HasMutationObserved &&
                !facts.HasCommitted &&
                !facts.HasFailure &&
                !facts.HasRecoveryRequired => null,
            StepCommittedEvent when
                facts.HasMutationObserved &&
                !facts.HasCommitted &&
                !facts.HasFailure &&
                !facts.HasRecoveryRequired => null,
            StepVerifiedEvent when
                facts.HasCommitted &&
                !facts.HasFailure &&
                !facts.HasRecoveryRequired => null,
            StepFailedEvent when
                !facts.HasFailure &&
                !facts.HasRecoveryRequired => null,
            StepRecoveryRequiredEvent when
                !facts.HasRecoveryRequired => null,
            _ => "The event violates the append-only step transition order.",
        };
    }

    private bool PreviousStepsAreVerified(int stepSequence)
    {
        for (int previous = 1; previous < stepSequence; previous++)
        {
            if (AssessStep(previous).Status != StepRecoveryStatus.Verified)
            {
                return false;
            }
        }

        return true;
    }

    private StepFacts GetFacts(int stepSequence)
    {
        bool hasIntent = false;
        bool hasMutationObserved = false;
        bool hasCommitted = false;
        bool hasVerified = false;
        bool hasFailure = false;
        bool hasRecoveryRequired = false;
        bool hasRollback = false;
        string? failureCode = null;
        string? recoveryReasonCode = null;
        foreach (ExecutionJournalEvent journalEvent in Events.Where(
                     candidate => candidate.StepSequence == stepSequence))
        {
            switch (journalEvent)
            {
                case StepIntentRecordedEvent:
                case RecoveryStepIntentRecordedEvent:
                    hasIntent = true;
                    break;
                case StepMutationObservedEvent:
                    hasMutationObserved = true;
                    break;
                case StepCommittedEvent:
                    hasCommitted = true;
                    break;
                case StepVerifiedEvent:
                    hasVerified = true;
                    break;
                case StepFailedEvent failed:
                    hasFailure = true;
                    failureCode = failed.FailureCode;
                    break;
                case StepRecoveryRequiredEvent recovery:
                    hasRecoveryRequired = true;
                    recoveryReasonCode = recovery.ReasonCode;
                    break;
                case StepRolledBackEvent:
                    hasRollback = true;
                    break;
            }
        }

        return new StepFacts(
            hasIntent,
            hasMutationObserved,
            hasCommitted,
            hasVerified,
            hasFailure,
            hasRecoveryRequired,
            hasRollback,
            failureCode,
            recoveryReasonCode);
    }

    private static DomainResult<ExecutionJournal> Failure(string code, string message) =>
        DomainResult.Failure<ExecutionJournal>(code, message);

    private static JournalInverseKind ExpectedInverseKind(PlannedFileOperation operation) =>
        operation.Primitive switch
        {
            FilePrimitive.EnsureDirectory when
                operation.DestinationPrecondition == DestinationPrecondition.ExistingDirectory =>
                JournalInverseKind.None,
            FilePrimitive.EnsureDirectory => JournalInverseKind.RemoveCreatedEntry,
            FilePrimitive.RenameFile => JournalInverseKind.RenameBack,
            FilePrimitive.MoveFile => JournalInverseKind.MoveBack,
            FilePrimitive.CopyFile => JournalInverseKind.RemoveCreatedEntry,
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private sealed record StepFacts(
        bool HasIntent,
        bool HasMutationObserved,
        bool HasCommitted,
        bool HasVerified,
        bool HasFailure,
        bool HasRecoveryRequired,
        bool HasRollback,
        string? FailureCode,
        string? RecoveryReasonCode);
}
