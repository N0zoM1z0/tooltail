using System.Security;
using Tooltail.Application.Abstractions;
using Tooltail.Domain.Common;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Features.FileSkills.Execution;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Features.FileSkills.Snapshots;

namespace Tooltail.Features.FileSkills.Undo;

internal sealed class FileRecoveryExecutor
{
    private readonly IClock clock;
    private readonly IExecutionAuthoritySource authoritySource;
    private readonly IExecutionJournalStore journalStore;
    private readonly FolderSnapshotService snapshotService;
    private readonly IFileMutationEngine mutationEngine;
    private readonly PermissionGateway permissionGateway;
    private readonly RecoveryPathPreconditionValidator preconditionValidator;
    private readonly IRecoveryExecutionFaultInjector faultInjector;
    private readonly FileExecutionLimits limits;

    public FileRecoveryExecutor(
        IClock clock,
        IExecutionAuthoritySource authoritySource,
        IExecutionJournalStore journalStore,
        WindowsPathSafetyService pathSafety,
        FolderSnapshotService snapshotService,
        IFileMutationEngine mutationEngine,
        IRecoveryExecutionFaultInjector? faultInjector = null,
        FileExecutionLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(authoritySource);
        ArgumentNullException.ThrowIfNull(journalStore);
        ArgumentNullException.ThrowIfNull(pathSafety);
        ArgumentNullException.ThrowIfNull(snapshotService);
        ArgumentNullException.ThrowIfNull(mutationEngine);
        this.clock = clock;
        this.authoritySource = authoritySource;
        this.journalStore = journalStore;
        this.snapshotService = snapshotService;
        this.mutationEngine = mutationEngine;
        this.faultInjector = faultInjector ?? NoRecoveryExecutionFaultInjector.Instance;
        this.limits = limits ?? FileExecutionLimits.Default;
        permissionGateway = new PermissionGateway(clock);
        preconditionValidator = new RecoveryPathPreconditionValidator(
            pathSafety,
            this.limits.MaximumSourceFileBytes);
    }

    public async Task<UndoExecutionResult> ExecuteAsync(
        UndoExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        DateTimeOffset startedUtc = clock.UtcNow;
        if (!IsValidUtc(startedUtc))
        {
            return Result(
                request,
                UndoExecutionStatus.AuthorityDenied,
                "permission.non_utc_time");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Result(
                request,
                UndoExecutionStatus.Cancelled,
                "undo.cancelled");
        }

        string? originalFailure = ValidateOriginalJournal(request);
        if (originalFailure is not null)
        {
            return Result(
                request,
                UndoExecutionStatus.PreconditionFailed,
                originalFailure);
        }

        AuthorityCheck initialAuthority = await ReadAndValidateAuthorityAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        if (!initialAuthority.IsSuccess)
        {
            return Result(
                request,
                initialAuthority.IsCancelled
                    ? UndoExecutionStatus.Cancelled
                    : UndoExecutionStatus.AuthorityDenied,
                initialAuthority.ReasonCode);
        }

        ExecutionJournal recoveryJournal;
        try
        {
            recoveryJournal = ExecutionJournal.OpenRecovery(
                request.ExecutionId,
                request.Plan,
                clock.UtcNow);
        }
        catch (ArgumentException)
        {
            return Result(
                request,
                UndoExecutionStatus.AuthorityDenied,
                "undo.journal_open_invalid");
        }

        JournalWriteResult opened = await journalStore.CreateAsync(
            recoveryJournal,
            request.Authorization.ConsumedApproval,
            CancellationToken.None).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return Result(
                request,
                UndoExecutionStatus.PersistenceFailed,
                opened.FailureCode ?? "undo.journal_open_failed",
                recoveryJournal);
        }

        Reach(request, FileExecutionBoundary.JournalOpened);
        ExecutionJournal originalJournal = request.OriginalJournal;
        List<VerifiedRecoveryStepEvidence> verifiedSteps = [];
        foreach (PlannedRecoveryOperation operation in request.Plan.Definition.Operations)
        {
            if (cancellationToken.IsCancellationRequested || DurationExceeded(startedUtc))
            {
                return Result(
                    request,
                    UndoExecutionStatus.Cancelled,
                    cancellationToken.IsCancellationRequested
                        ? "undo.cancelled"
                        : "undo.duration_exceeded",
                    recoveryJournal,
                    originalJournal,
                    operation.Sequence,
                    verifiedSteps);
            }

            if (originalJournal.AssessStep(operation.OriginalStepSequence).Status !=
                StepRecoveryStatus.Verified)
            {
                return Result(
                    request,
                    UndoExecutionStatus.PreconditionFailed,
                    "undo.original_step_not_verified",
                    recoveryJournal,
                    originalJournal,
                    operation.Sequence,
                    verifiedSteps);
            }

            AuthorityCheck authority = await ReadAndValidateAuthorityAsync(
                request,
                cancellationToken).ConfigureAwait(false);
            if (!authority.IsSuccess)
            {
                return Result(
                    request,
                    authority.IsCancelled
                        ? UndoExecutionStatus.Cancelled
                        : UndoExecutionStatus.AuthorityDenied,
                    authority.ReasonCode,
                    recoveryJournal,
                    originalJournal,
                    operation.Sequence,
                    verifiedSteps);
            }

            FolderSnapshot before = await snapshotService.CaptureAsync(
                request.Root,
                authority.State!.Grant,
                cancellationToken).ConfigureAwait(false);
            if (!before.IsComplete)
            {
                return Result(
                    request,
                    cancellationToken.IsCancellationRequested
                        ? UndoExecutionStatus.Cancelled
                        : UndoExecutionStatus.PreconditionFailed,
                    before.ReasonCode ?? "undo.baseline_snapshot_failed",
                    recoveryJournal,
                    originalJournal,
                    operation.Sequence,
                    verifiedSteps);
            }

            RecoveryPreconditionResult prepared = await preconditionValidator.PrepareAsync(
                request.Root,
                operation,
                cancellationToken).ConfigureAwait(false);
            if (!prepared.IsSuccess)
            {
                return Result(
                    request,
                    prepared.ReasonCode == "undo.cancelled"
                        ? UndoExecutionStatus.Cancelled
                        : UndoExecutionStatus.PreconditionFailed,
                    prepared.ReasonCode,
                    recoveryJournal,
                    originalJournal,
                    operation.Sequence,
                    verifiedSteps);
            }

            JournalAppendResult intent = await AppendAsync(
                recoveryJournal,
                (sequence, occurredUtc) => new RecoveryStepIntentRecordedEvent(
                    recoveryJournal.ExecutionId,
                    sequence,
                    occurredUtc,
                    operation.Sequence,
                    operation.Primitive,
                    operation.OriginalStepSequence,
                    request.Plan.Fingerprint),
                CancellationToken.None).ConfigureAwait(false);
            if (!intent.IsSuccess)
            {
                return Result(
                    request,
                    UndoExecutionStatus.PersistenceFailed,
                    intent.ReasonCode,
                    recoveryJournal,
                    originalJournal,
                    operation.Sequence,
                    verifiedSteps);
            }

            recoveryJournal = intent.Journal!;
            Reach(request, FileExecutionBoundary.StepIntentPersisted, operation.Sequence);
            Reach(request, FileExecutionBoundary.BeforePrimitive, operation.Sequence);
            if (cancellationToken.IsCancellationRequested)
            {
                return await FailAfterIntentAsync(
                    request,
                    recoveryJournal,
                    originalJournal,
                    operation.Sequence,
                    "undo.cancelled",
                    mutationObserved: false,
                    verifiedSteps).ConfigureAwait(false);
            }

            AuthorityCheck immediateAuthority = await ReadAndValidateAuthorityAsync(
                request,
                CancellationToken.None).ConfigureAwait(false);
            if (!immediateAuthority.IsSuccess)
            {
                return await FailAfterIntentAsync(
                    request,
                    recoveryJournal,
                    originalJournal,
                    operation.Sequence,
                    immediateAuthority.ReasonCode,
                    mutationObserved: false,
                    verifiedSteps).ConfigureAwait(false);
            }

            RecoveryPreconditionResult revalidated = await preconditionValidator.RevalidateAsync(
                request.Root,
                prepared.Paths!,
                operation,
                CancellationToken.None).ConfigureAwait(false);
            if (!revalidated.IsSuccess)
            {
                return await FailAfterIntentAsync(
                    request,
                    recoveryJournal,
                    originalJournal,
                    operation.Sequence,
                    revalidated.ReasonCode,
                    mutationObserved: false,
                    verifiedSteps).ConfigureAwait(false);
            }

            RecoveryPreconditionResult finalPaths = await preconditionValidator.RevalidateAsync(
                request.Root,
                revalidated.Paths!,
                operation,
                CancellationToken.None).ConfigureAwait(false);
            if (!finalPaths.IsSuccess)
            {
                return await FailAfterIntentAsync(
                    request,
                    recoveryJournal,
                    originalJournal,
                    operation.Sequence,
                    finalPaths.ReasonCode,
                    mutationObserved: false,
                    verifiedSteps).ConfigureAwait(false);
            }

            FileMutationPreparationResult mutationPreparation;
            try
            {
                mutationPreparation = AllowlistedRecoveryPrimitiveExecutor.Prepare(
                    mutationEngine,
                    operation,
                    finalPaths.Paths!);
            }
            catch (Exception exception) when (IsExpectedPrimitiveFailure(exception))
            {
                return await FailAfterIntentAsync(
                    request,
                    recoveryJournal,
                    originalJournal,
                    operation.Sequence,
                    PrimitiveFailureCode(exception),
                    mutationObserved: false,
                    verifiedSteps).ConfigureAwait(false);
            }

            if (!mutationPreparation.IsSuccess)
            {
                return await FailAfterIntentAsync(
                    request,
                    recoveryJournal,
                    originalJournal,
                    operation.Sequence,
                    PrimitiveFailureCode(mutationPreparation.FailureKind),
                    mutationObserved: false,
                    verifiedSteps).ConfigureAwait(false);
            }

            using IPreparedFileMutation preparedMutation =
                mutationPreparation.PreparedMutation!;
            AuthorityCheck finalAuthority = await ReadAndValidateAuthorityAsync(
                request,
                CancellationToken.None).ConfigureAwait(false);
            if (!finalAuthority.IsSuccess)
            {
                return await FailAfterIntentAsync(
                    request,
                    recoveryJournal,
                    originalJournal,
                    operation.Sequence,
                    finalAuthority.ReasonCode,
                    mutationObserved: false,
                    verifiedSteps).ConfigureAwait(false);
            }

            FileMutationResult mutation;
            try
            {
                mutation = preparedMutation.Execute();
            }
            catch (Exception exception) when (IsExpectedPrimitiveFailure(exception))
            {
                FolderSnapshot failedAfter = await snapshotService.CaptureAsync(
                    request.Root,
                    finalAuthority.State!.Grant,
                    CancellationToken.None).ConfigureAwait(false);
                bool mutationObserved = failedAfter.IsComplete &&
                    RecoveryStepVerifier.Verify(before, failedAfter, operation).IsSuccess;
                return await FailAfterIntentAsync(
                    request,
                    recoveryJournal,
                    originalJournal,
                    operation.Sequence,
                    PrimitiveFailureCode(exception),
                    mutationObserved,
                    verifiedSteps).ConfigureAwait(false);
            }

            if (!mutation.IsSuccess)
            {
                return await FailAfterIntentAsync(
                    request,
                    recoveryJournal,
                    originalJournal,
                    operation.Sequence,
                    PrimitiveFailureCode(mutation.FailureKind),
                    mutation.MutationMayHaveOccurred,
                    verifiedSteps).ConfigureAwait(false);
            }

            if (operation.Primitive != RecoveryPrimitive.RemoveCreatedEntry &&
                mutation.Evidence is null)
            {
                return await FailAfterIntentAsync(
                    request,
                    recoveryJournal,
                    originalJournal,
                    operation.Sequence,
                    "undo.mutation_evidence_missing",
                    mutationObserved: true,
                    verifiedSteps).ConfigureAwait(false);
            }

            Reach(request, FileExecutionBoundary.AfterPrimitive, operation.Sequence);
            JournalAppendResult observed = await AppendAsync(
                recoveryJournal,
                (sequence, occurredUtc) => new StepMutationObservedEvent(
                    recoveryJournal.ExecutionId,
                    sequence,
                    occurredUtc,
                    operation.Sequence),
                CancellationToken.None).ConfigureAwait(false);
            if (!observed.IsSuccess)
            {
                return Result(
                    request,
                    UndoExecutionStatus.PersistenceFailed,
                    observed.ReasonCode,
                    recoveryJournal,
                    originalJournal,
                    operation.Sequence,
                    verifiedSteps,
                    requiresInspection: true);
            }

            recoveryJournal = observed.Journal!;
            Reach(request, FileExecutionBoundary.MutationObservedPersisted, operation.Sequence);
            JournalAppendResult committed = await AppendAsync(
                recoveryJournal,
                (sequence, occurredUtc) => new StepCommittedEvent(
                    recoveryJournal.ExecutionId,
                    sequence,
                    occurredUtc,
                    operation.Sequence),
                CancellationToken.None).ConfigureAwait(false);
            if (!committed.IsSuccess)
            {
                return Result(
                    request,
                    UndoExecutionStatus.PersistenceFailed,
                    committed.ReasonCode,
                    recoveryJournal,
                    originalJournal,
                    operation.Sequence,
                    verifiedSteps,
                    requiresInspection: true);
            }

            recoveryJournal = committed.Journal!;
            Reach(request, FileExecutionBoundary.StepCommittedPersisted, operation.Sequence);
            AuthorityCheck verificationAuthority = await ReadAndValidateAuthorityAsync(
                request,
                CancellationToken.None).ConfigureAwait(false);
            if (!verificationAuthority.IsSuccess)
            {
                return await FailAfterIntentAsync(
                    request,
                    recoveryJournal,
                    originalJournal,
                    operation.Sequence,
                    verificationAuthority.ReasonCode,
                    mutationObserved: false,
                    verifiedSteps).ConfigureAwait(false);
            }

            FolderSnapshot after = await snapshotService.CaptureAsync(
                request.Root,
                verificationAuthority.State!.Grant,
                CancellationToken.None).ConfigureAwait(false);
            RecoveryStepVerification verification = RecoveryStepVerifier.Verify(
                before,
                after,
                operation,
                mutation.Evidence);
            if (!verification.IsSuccess)
            {
                return await FailAfterIntentAsync(
                    request,
                    recoveryJournal,
                    originalJournal,
                    operation.Sequence,
                    verification.ReasonCode,
                    mutationObserved: false,
                    verifiedSteps).ConfigureAwait(false);
            }

            JournalAppendResult verified = await AppendAsync(
                recoveryJournal,
                (sequence, occurredUtc) => new StepVerifiedEvent(
                    recoveryJournal.ExecutionId,
                    sequence,
                    occurredUtc,
                    operation.Sequence),
                CancellationToken.None).ConfigureAwait(false);
            if (!verified.IsSuccess)
            {
                return Result(
                    request,
                    UndoExecutionStatus.PersistenceFailed,
                    verified.ReasonCode,
                    recoveryJournal,
                    originalJournal,
                    operation.Sequence,
                    verifiedSteps,
                    requiresInspection: true);
            }

            recoveryJournal = verified.Journal!;
            verifiedSteps.Add(verification.Evidence!);
            Reach(request, FileExecutionBoundary.StepVerifiedPersisted, operation.Sequence);
            JournalAppendResult linked = await AppendOriginalRollbackAsync(
                originalJournal,
                operation.OriginalStepSequence,
                recoveryJournal.ExecutionId).ConfigureAwait(false);
            if (!linked.IsSuccess)
            {
                return Result(
                    request,
                    UndoExecutionStatus.PersistenceFailed,
                    linked.ReasonCode,
                    recoveryJournal,
                    originalJournal,
                    operation.Sequence,
                    verifiedSteps,
                    requiresInspection: true);
            }

            originalJournal = linked.Journal!;
            Reach(
                request,
                FileExecutionBoundary.OriginalStepRollbackLinked,
                operation.Sequence);
        }

        DateTimeOffset completedUtc = clock.UtcNow;
        if (!IsValidUtc(completedUtc) ||
            completedUtc < recoveryJournal.Events[^1].OccurredUtc ||
            completedUtc < originalJournal.Events[^1].OccurredUtc)
        {
            return Result(
                request,
                UndoExecutionStatus.VerificationFailed,
                "undo.completion_time_invalid",
                recoveryJournal,
                originalJournal,
                verifiedSteps: verifiedSteps);
        }

        DomainResult<RecoveryExecutionReceipt> receiptResult =
            RecoveryExecutionReceipt.CreateVerified(
                request.ReceiptId,
                request.Plan,
                recoveryJournal,
                originalJournal,
                completedUtc,
                verifiedSteps);
        if (!receiptResult.IsSuccess)
        {
            return Result(
                request,
                UndoExecutionStatus.VerificationFailed,
                receiptResult.Error!.Code,
                recoveryJournal,
                originalJournal,
                verifiedSteps: verifiedSteps);
        }

        RecoveryExecutionReceipt receipt = receiptResult.Value!;
        JournalWriteResult stored = await journalStore.StoreRecoveryReceiptAsync(
            receipt,
            CancellationToken.None).ConfigureAwait(false);
        if (!stored.IsSuccess)
        {
            return Result(
                request,
                UndoExecutionStatus.PersistenceFailed,
                stored.FailureCode ?? "undo.receipt_write_failed",
                recoveryJournal,
                originalJournal,
                receipt: receipt,
                verifiedSteps: verifiedSteps);
        }

        Reach(request, FileExecutionBoundary.ReceiptPersisted);
        return Result(
            request,
            UndoExecutionStatus.Verified,
            "undo.verified",
            recoveryJournal,
            originalJournal,
            receipt: receipt,
            verifiedSteps: verifiedSteps);
    }

    private async Task<UndoExecutionResult> FailAfterIntentAsync(
        UndoExecutionRequest request,
        ExecutionJournal recoveryJournal,
        ExecutionJournal originalJournal,
        int stepSequence,
        string failureCode,
        bool mutationObserved,
        IReadOnlyCollection<VerifiedRecoveryStepEvidence> verifiedSteps)
    {
        if (mutationObserved)
        {
            JournalAppendResult observed = await AppendAsync(
                recoveryJournal,
                (sequence, occurredUtc) => new StepMutationObservedEvent(
                    recoveryJournal.ExecutionId,
                    sequence,
                    occurredUtc,
                    stepSequence),
                CancellationToken.None).ConfigureAwait(false);
            if (!observed.IsSuccess)
            {
                return Result(
                    request,
                    UndoExecutionStatus.PersistenceFailed,
                    observed.ReasonCode,
                    recoveryJournal,
                    originalJournal,
                    stepSequence,
                    verifiedSteps,
                    requiresInspection: true);
            }

            recoveryJournal = observed.Journal!;
            Reach(request, FileExecutionBoundary.MutationObservedPersisted, stepSequence);
        }

        JournalAppendResult failed = await AppendAsync(
            recoveryJournal,
            (sequence, occurredUtc) => new StepFailedEvent(
                recoveryJournal.ExecutionId,
                sequence,
                occurredUtc,
                stepSequence,
                NormalizeReasonCode(failureCode)),
            CancellationToken.None).ConfigureAwait(false);
        if (!failed.IsSuccess)
        {
            return Result(
                request,
                UndoExecutionStatus.PersistenceFailed,
                failed.ReasonCode,
                recoveryJournal,
                originalJournal,
                stepSequence,
                verifiedSteps,
                requiresInspection: true);
        }

        recoveryJournal = failed.Journal!;
        Reach(request, FileExecutionBoundary.StepFailedPersisted, stepSequence);
        JournalAppendResult recovery = await AppendAsync(
            recoveryJournal,
            (sequence, occurredUtc) => new StepRecoveryRequiredEvent(
                recoveryJournal.ExecutionId,
                sequence,
                occurredUtc,
                stepSequence,
                "undo.inspect_before_recovery"),
            CancellationToken.None).ConfigureAwait(false);
        if (!recovery.IsSuccess)
        {
            return Result(
                request,
                UndoExecutionStatus.PersistenceFailed,
                recovery.ReasonCode,
                recoveryJournal,
                originalJournal,
                stepSequence,
                verifiedSteps,
                requiresInspection: true);
        }

        recoveryJournal = recovery.Journal!;
        Reach(request, FileExecutionBoundary.RecoveryRequiredPersisted, stepSequence);
        return Result(
            request,
            UndoExecutionStatus.RecoveryRequired,
            failureCode,
            recoveryJournal,
            originalJournal,
            stepSequence,
            verifiedSteps,
            requiresInspection: true);
    }

    private async ValueTask<AuthorityCheck> ReadAndValidateAuthorityAsync(
        UndoExecutionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            RecoveryPlanDefinition definition = request.Plan.Definition;
            ExecutionAuthorityState? state = await authoritySource.ReadCurrentAsync(
                definition.SkillId,
                definition.SkillVersion,
                definition.GrantId,
                cancellationToken).ConfigureAwait(false);
            if (state is null)
            {
                return AuthorityCheck.Failure("permission.authority_unavailable");
            }

            DomainResult<ExecutionAuthorization> checkedAuthority =
                permissionGateway.RevalidateUndo(
                    request.Authorization,
                    request.Plan,
                    state.SkillVersion,
                    state.Grant);
            return checkedAuthority.IsSuccess
                ? AuthorityCheck.Success(state)
                : AuthorityCheck.Failure(checkedAuthority.Error!.Code);
        }
        catch (OperationCanceledException)
        {
            return AuthorityCheck.Cancelled;
        }
    }

    private async ValueTask<JournalAppendResult> AppendAsync(
        ExecutionJournal journal,
        Func<long, DateTimeOffset, ExecutionJournalEvent> eventFactory,
        CancellationToken cancellationToken)
    {
        DateTimeOffset occurredUtc = clock.UtcNow;
        if (!IsValidUtc(occurredUtc) || occurredUtc < journal.Events[^1].OccurredUtc)
        {
            return JournalAppendResult.Failure("undo.journal_time_invalid");
        }

        ExecutionJournalEvent journalEvent;
        try
        {
            journalEvent = eventFactory(journal.Events.Count + 1L, occurredUtc);
        }
        catch (ArgumentException)
        {
            return JournalAppendResult.Failure("undo.journal_event_invalid");
        }

        DomainResult<ExecutionJournal> appended = journal.Append(journalEvent);
        if (!appended.IsSuccess)
        {
            return JournalAppendResult.Failure(appended.Error!.Code);
        }

        JournalWriteResult persisted = await journalStore.AppendAsync(
            journalEvent,
            cancellationToken).ConfigureAwait(false);
        return persisted.IsSuccess
            ? JournalAppendResult.Success(appended.Value!)
            : JournalAppendResult.Failure(
                persisted.FailureCode ?? "undo.journal_write_failed");
    }

    private ValueTask<JournalAppendResult> AppendOriginalRollbackAsync(
        ExecutionJournal originalJournal,
        int originalStepSequence,
        ExecutionId recoveryExecutionId) =>
        AppendAsync(
            originalJournal,
            (sequence, occurredUtc) => new StepRolledBackEvent(
                originalJournal.ExecutionId,
                sequence,
                occurredUtc,
                originalStepSequence,
                recoveryExecutionId),
            CancellationToken.None);

    private static string? ValidateOriginalJournal(UndoExecutionRequest request)
    {
        RecoveryPlanDefinition definition = request.Plan.Definition;
        if (request.OriginalJournal.Kind != ExecutionJournalKind.Standard ||
            request.OriginalJournal.ExecutionId != definition.OriginalExecutionId ||
            request.OriginalJournal.PlanId != definition.OriginalPlanId ||
            request.OriginalJournal.PlanFingerprint != definition.OriginalPlanFingerprint)
        {
            return "undo.original_journal_mismatch";
        }

        return definition.Operations.Any(operation =>
            request.OriginalJournal.AssessStep(operation.OriginalStepSequence).Status !=
                StepRecoveryStatus.Verified)
            ? "undo.original_step_not_verified"
            : null;
    }

    private static bool IsExpectedPrimitiveFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or
            NotSupportedException;

    private static string PrimitiveFailureCode(Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException or SecurityException => "undo.primitive_access_denied",
            NotSupportedException => "undo.primitive_not_supported",
            _ => "undo.primitive_io_failure",
        };

    private static string PrimitiveFailureCode(FileMutationFailureKind failureKind) =>
        failureKind switch
        {
            FileMutationFailureKind.UnsupportedPlatform => "undo.primitive_not_supported",
            FileMutationFailureKind.InvalidRequest => "undo.primitive_not_allowed",
            FileMutationFailureKind.RootChanged => "undo.root_changed",
            FileMutationFailureKind.PathChanged => "undo.atomic_path_changed",
            FileMutationFailureKind.SourceMissing => "undo.source_missing",
            FileMutationFailureKind.SourceChanged => "undo.source_identity_changed",
            FileMutationFailureKind.DestinationExists => "undo.destination_exists",
            FileMutationFailureKind.AccessDenied => "undo.primitive_access_denied",
            FileMutationFailureKind.DirectoryNotEmpty => "undo.created_directory_not_empty",
            FileMutationFailureKind.LimitExceeded => "undo.source_file_limit_exceeded",
            FileMutationFailureKind.CleanupFailed => "undo.primitive_cleanup_failed",
            _ => "undo.primitive_io_failure",
        };

    private static string NormalizeReasonCode(string reasonCode) =>
        !string.IsNullOrWhiteSpace(reasonCode) &&
        reasonCode.Length <= 128 &&
        reasonCode.All(static value =>
            value is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')
            ? reasonCode
            : "undo.failure";

    private void Reach(
        UndoExecutionRequest request,
        FileExecutionBoundary boundary,
        int? stepSequence = null) =>
        faultInjector.Reach(
            new RecoveryExecutionBoundaryContext(
                request.ExecutionId,
                boundary,
                stepSequence));

    private bool DurationExceeded(DateTimeOffset startedUtc)
    {
        DateTimeOffset nowUtc = clock.UtcNow;
        return !IsValidUtc(nowUtc) ||
            nowUtc < startedUtc ||
            nowUtc - startedUtc > limits.MaximumDuration;
    }

    private static bool IsValidUtc(DateTimeOffset value) =>
        value.Offset == TimeSpan.Zero;

    private static UndoExecutionResult Result(
        UndoExecutionRequest request,
        UndoExecutionStatus status,
        string reasonCode,
        ExecutionJournal? recoveryJournal = null,
        ExecutionJournal? originalJournal = null,
        int? failedStepSequence = null,
        IEnumerable<VerifiedRecoveryStepEvidence>? verifiedSteps = null,
        RecoveryExecutionReceipt? receipt = null,
        bool requiresInspection = false) =>
        new(
            status,
            reasonCode,
            recoveryJournal,
            originalJournal ?? request.OriginalJournal,
            receipt,
            failedStepSequence,
            verifiedSteps ?? [],
            Residuals(
                request.Plan,
                originalJournal ?? request.OriginalJournal,
                failedStepSequence,
                requiresInspection));

    private static string[] Residuals(
        RecoveryPlan plan,
        ExecutionJournal originalJournal,
        int? failedStepSequence,
        bool requiresInspection)
    {
        List<string> residuals = plan.Definition.Operations
            .Where(operation =>
                originalJournal.AssessStep(operation.OriginalStepSequence).Status !=
                    StepRecoveryStatus.RolledBack)
            .Select(operation => $"undo.original_step_{operation.OriginalStepSequence}_remains")
            .ToList();
        if (requiresInspection && failedStepSequence is not null)
        {
            residuals.Add($"undo.recovery_step_{failedStepSequence.Value}_requires_inspection");
        }

        return residuals.Distinct(StringComparer.Ordinal).ToArray();
    }

    private sealed record AuthorityCheck(
        bool IsSuccess,
        bool IsCancelled,
        string ReasonCode,
        ExecutionAuthorityState? State)
    {
        public static AuthorityCheck Success(ExecutionAuthorityState state) =>
            new(true, false, "permission.authority_current", state);

        public static AuthorityCheck Failure(string reasonCode) =>
            new(false, false, reasonCode, null);

        public static AuthorityCheck Cancelled { get; } =
            new(false, true, "undo.cancelled", null);
    }

    private sealed record JournalAppendResult(
        bool IsSuccess,
        string ReasonCode,
        ExecutionJournal? Journal)
    {
        public static JournalAppendResult Success(ExecutionJournal journal) =>
            new(true, "undo.journal_appended", journal);

        public static JournalAppendResult Failure(string reasonCode) =>
            new(false, reasonCode, null);
    }
}
