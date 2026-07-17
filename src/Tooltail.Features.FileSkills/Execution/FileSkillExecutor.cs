using System.Security;
using Tooltail.Application.Abstractions;
using Tooltail.Domain.Common;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Permissions;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Features.FileSkills.Snapshots;
using Tooltail.Features.FileSkills.Undo;

namespace Tooltail.Features.FileSkills.Execution;

public sealed class FileSkillExecutor
{
    private readonly IClock clock;
    private readonly IExecutionAuthoritySource authoritySource;
    private readonly IExecutionJournalStore journalStore;
    private readonly FolderSnapshotService snapshotService;
    private readonly WindowsPathSafetyService pathSafety;
    private readonly IFileMutationEngine mutationEngine;
    private readonly PermissionGateway permissionGateway;
    private readonly ExecutionPathPreconditionValidator preconditionValidator;
    private readonly IFileExecutionFaultInjector faultInjector;
    private readonly FileExecutionLimits limits;

    public FileSkillExecutor(
        IClock clock,
        IExecutionAuthoritySource authoritySource,
        IExecutionJournalStore journalStore,
        WindowsPathSafetyService pathSafety,
        FolderSnapshotService snapshotService,
        IFileMutationEngine mutationEngine,
        IFileExecutionFaultInjector? faultInjector = null,
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
        this.pathSafety = pathSafety;
        this.snapshotService = snapshotService;
        this.mutationEngine = mutationEngine;
        this.faultInjector = faultInjector ?? NoFileExecutionFaultInjector.Instance;
        this.limits = limits ?? FileExecutionLimits.Default;
        permissionGateway = new PermissionGateway(clock);
        preconditionValidator = new ExecutionPathPreconditionValidator(
            pathSafety,
            this.limits);
    }

    public Task<UndoExecutionResult> ExecuteUndoAsync(
        UndoExecutionRequest request,
        IRecoveryExecutionFaultInjector? recoveryFaultInjector = null,
        CancellationToken cancellationToken = default) =>
        new FileRecoveryExecutor(
            clock,
            authoritySource,
            journalStore,
            pathSafety,
            snapshotService,
            mutationEngine,
            recoveryFaultInjector,
            limits).ExecuteAsync(request, cancellationToken);

    public async Task<FileExecutionResult> ExecuteAsync(
        FileExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        DateTimeOffset startedUtc = clock.UtcNow;
        if (!IsValidUtc(startedUtc))
        {
            return Result(
                request,
                FileExecutionStatus.AuthorityDenied,
                "permission.non_utc_time");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Result(
                request,
                FileExecutionStatus.Cancelled,
                "execution.cancelled");
        }

        AuthorityCheck initialAuthority = await ReadAndValidateAuthorityAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        if (!initialAuthority.IsSuccess)
        {
            return Result(
                request,
                initialAuthority.IsCancelled
                    ? FileExecutionStatus.Cancelled
                    : FileExecutionStatus.AuthorityDenied,
                initialAuthority.ReasonCode);
        }

        string? verificationAuthorityFailure = ValidateVerificationAuthority(
            request.Plan,
            initialAuthority.State!.Grant,
            clock.UtcNow);
        if (verificationAuthorityFailure is not null)
        {
            return Result(
                request,
                FileExecutionStatus.AuthorityDenied,
                verificationAuthorityFailure);
        }

        ExecutionJournal journal;
        try
        {
            journal = ExecutionJournal.Open(
                request.ExecutionId,
                request.Plan,
                clock.UtcNow);
        }
        catch (ArgumentException)
        {
            return Result(
                request,
                FileExecutionStatus.AuthorityDenied,
                "execution.journal_open_time_invalid");
        }

        JournalWriteResult opened = await journalStore.CreateAsync(
            journal,
            request.Authorization.ConsumedApproval,
            CancellationToken.None).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return Result(
                request,
                FileExecutionStatus.PersistenceFailed,
                opened.FailureCode ?? "execution.journal_open_failed",
                journal);
        }

        Reach(request, FileExecutionBoundary.JournalOpened);
        List<VerifiedStepEvidence> verifiedSteps = [];
        foreach (PlannedFileOperation operation in request.Plan.Definition.Operations)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Result(
                    request,
                    FileExecutionStatus.Cancelled,
                    "execution.cancelled",
                    journal,
                    verifiedSteps: verifiedSteps);
            }

            if (DurationExceeded(startedUtc))
            {
                return Result(
                    request,
                    FileExecutionStatus.Cancelled,
                    "execution.duration_exceeded",
                    journal,
                    verifiedSteps: verifiedSteps);
            }

            AuthorityCheck authority = await ReadAndValidateAuthorityAsync(
                request,
                cancellationToken).ConfigureAwait(false);
            if (!authority.IsSuccess)
            {
                return Result(
                    request,
                    authority.IsCancelled
                        ? FileExecutionStatus.Cancelled
                        : FileExecutionStatus.AuthorityDenied,
                    authority.ReasonCode,
                    journal,
                    operation.Sequence,
                    verifiedSteps: verifiedSteps);
            }

            verificationAuthorityFailure = ValidateVerificationAuthority(
                request.Plan,
                authority.State!.Grant,
                clock.UtcNow);
            if (verificationAuthorityFailure is not null)
            {
                return Result(
                    request,
                    FileExecutionStatus.AuthorityDenied,
                    verificationAuthorityFailure,
                    journal,
                    operation.Sequence,
                    verifiedSteps: verifiedSteps);
            }

            FolderSnapshot before = await snapshotService.CaptureAsync(
                request.Root,
                authority.State.Grant,
                cancellationToken).ConfigureAwait(false);
            if (!before.IsComplete)
            {
                return Result(
                    request,
                    cancellationToken.IsCancellationRequested
                        ? FileExecutionStatus.Cancelled
                        : FileExecutionStatus.PreconditionFailed,
                    before.ReasonCode ?? "execution.baseline_snapshot_failed",
                    journal,
                    operation.Sequence,
                    verifiedSteps: verifiedSteps);
            }

            ExecutionPreconditionResult prepared = await preconditionValidator.PrepareAsync(
                request.Root,
                operation,
                cancellationToken).ConfigureAwait(false);
            if (!prepared.IsSuccess)
            {
                return Result(
                    request,
                    prepared.ReasonCode == "execution.cancelled"
                        ? FileExecutionStatus.Cancelled
                        : FileExecutionStatus.PreconditionFailed,
                    prepared.ReasonCode,
                    journal,
                    operation.Sequence,
                    verifiedSteps: verifiedSteps);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Result(
                    request,
                    FileExecutionStatus.Cancelled,
                    "execution.cancelled",
                    journal,
                    operation.Sequence,
                    verifiedSteps: verifiedSteps);
            }

            JournalAppendResult intent = await AppendAsync(
                journal,
                (sequence, occurredUtc) => new StepIntentRecordedEvent(
                    journal.ExecutionId,
                    sequence,
                    occurredUtc,
                    operation.Sequence,
                    operation.Primitive,
                    request.Plan.Fingerprint,
                    journal.OperationInverseKinds[operation.Sequence - 1]),
                CancellationToken.None).ConfigureAwait(false);
            if (!intent.IsSuccess)
            {
                return Result(
                    request,
                    FileExecutionStatus.PersistenceFailed,
                    intent.ReasonCode,
                    journal,
                    operation.Sequence,
                    verifiedSteps: verifiedSteps);
            }

            journal = intent.Journal!;
            Reach(request, FileExecutionBoundary.StepIntentPersisted, operation.Sequence);
            Reach(request, FileExecutionBoundary.BeforePrimitive, operation.Sequence);

            if (cancellationToken.IsCancellationRequested)
            {
                return await FailAfterIntentAsync(
                    request,
                    journal,
                    operation.Sequence,
                    "execution.cancelled",
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
                    journal,
                    operation.Sequence,
                    immediateAuthority.ReasonCode,
                    mutationObserved: false,
                    verifiedSteps).ConfigureAwait(false);
            }

            verificationAuthorityFailure = ValidateVerificationAuthority(
                request.Plan,
                immediateAuthority.State!.Grant,
                clock.UtcNow);
            if (verificationAuthorityFailure is not null)
            {
                return await FailAfterIntentAsync(
                    request,
                    journal,
                    operation.Sequence,
                    verificationAuthorityFailure,
                    mutationObserved: false,
                    verifiedSteps).ConfigureAwait(false);
            }

            ExecutionPreconditionResult revalidated = await preconditionValidator.RevalidateAsync(
                prepared.Paths!,
                operation,
                CancellationToken.None).ConfigureAwait(false);
            if (!revalidated.IsSuccess)
            {
                return await FailAfterIntentAsync(
                    request,
                    journal,
                    operation.Sequence,
                    revalidated.ReasonCode,
                    mutationObserved: false,
                    verifiedSteps).ConfigureAwait(false);
            }

            FileMutationPreparationResult mutationPreparation;
            try
            {
                mutationPreparation = AllowlistedFilePrimitiveExecutor.Prepare(
                    mutationEngine,
                    operation,
                    revalidated.Paths!,
                    limits.MaximumSourceFileBytes);
            }
            catch (Exception exception) when (IsExpectedPrimitiveFailure(exception))
            {
                return await FailAfterIntentAsync(
                    request,
                    journal,
                    operation.Sequence,
                    PrimitiveFailureCode(exception),
                    mutationObserved: false,
                    verifiedSteps).ConfigureAwait(false);
            }

            if (!mutationPreparation.IsSuccess)
            {
                return await FailAfterIntentAsync(
                    request,
                    journal,
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
                    journal,
                    operation.Sequence,
                    finalAuthority.ReasonCode,
                    mutationObserved: false,
                    verifiedSteps).ConfigureAwait(false);
            }

            verificationAuthorityFailure = ValidateVerificationAuthority(
                request.Plan,
                finalAuthority.State!.Grant,
                clock.UtcNow);
            if (verificationAuthorityFailure is not null)
            {
                return await FailAfterIntentAsync(
                    request,
                    journal,
                    operation.Sequence,
                    verificationAuthorityFailure,
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
                    finalAuthority.State.Grant,
                    CancellationToken.None).ConfigureAwait(false);
                bool mutationObserved = failedAfter.IsComplete &&
                    ExecutionStepVerifier.Verify(before, failedAfter, operation).IsSuccess;
                return await FailAfterIntentAsync(
                    request,
                    journal,
                    operation.Sequence,
                    PrimitiveFailureCode(exception),
                    mutationObserved,
                    verifiedSteps).ConfigureAwait(false);
            }

            if (!mutation.IsSuccess)
            {
                FolderSnapshot failedAfter = await snapshotService.CaptureAsync(
                    request.Root,
                    finalAuthority.State.Grant,
                    CancellationToken.None).ConfigureAwait(false);
                bool mutationObserved = mutation.MutationMayHaveOccurred ||
                    (failedAfter.IsComplete &&
                     ExecutionStepVerifier.Verify(before, failedAfter, operation).IsSuccess);
                return await FailAfterIntentAsync(
                    request,
                    journal,
                    operation.Sequence,
                    PrimitiveFailureCode(mutation.FailureKind),
                    mutationObserved,
                    verifiedSteps).ConfigureAwait(false);
            }

            if (mutation.Evidence is null)
            {
                return await FailAfterIntentAsync(
                    request,
                    journal,
                    operation.Sequence,
                    "execution.mutation_evidence_missing",
                    mutationObserved: true,
                    verifiedSteps).ConfigureAwait(false);
            }

            Reach(request, FileExecutionBoundary.AfterPrimitive, operation.Sequence);
            JournalAppendResult observed = await AppendAsync(
                journal,
                (sequence, occurredUtc) => new StepMutationObservedEvent(
                    journal.ExecutionId,
                    sequence,
                    occurredUtc,
                    operation.Sequence),
                CancellationToken.None).ConfigureAwait(false);
            if (!observed.IsSuccess)
            {
                return Result(
                    request,
                    FileExecutionStatus.PersistenceFailed,
                    observed.ReasonCode,
                    journal,
                    operation.Sequence,
                    verifiedSteps: verifiedSteps);
            }

            journal = observed.Journal!;
            Reach(request, FileExecutionBoundary.MutationObservedPersisted, operation.Sequence);
            JournalAppendResult committed = await AppendAsync(
                journal,
                (sequence, occurredUtc) => new StepCommittedEvent(
                    journal.ExecutionId,
                    sequence,
                    occurredUtc,
                    operation.Sequence),
                CancellationToken.None).ConfigureAwait(false);
            if (!committed.IsSuccess)
            {
                return Result(
                    request,
                    FileExecutionStatus.PersistenceFailed,
                    committed.ReasonCode,
                    journal,
                    operation.Sequence,
                    verifiedSteps: verifiedSteps);
            }

            journal = committed.Journal!;
            Reach(request, FileExecutionBoundary.StepCommittedPersisted, operation.Sequence);

            AuthorityCheck verificationAuthority = await ReadAndValidateAuthorityAsync(
                request,
                CancellationToken.None).ConfigureAwait(false);
            if (!verificationAuthority.IsSuccess)
            {
                return await FailAfterIntentAsync(
                    request,
                    journal,
                    operation.Sequence,
                    verificationAuthority.ReasonCode,
                    mutationObserved: false,
                    verifiedSteps).ConfigureAwait(false);
            }

            verificationAuthorityFailure = ValidateVerificationAuthority(
                request.Plan,
                verificationAuthority.State!.Grant,
                clock.UtcNow);
            if (verificationAuthorityFailure is not null)
            {
                return await FailAfterIntentAsync(
                    request,
                    journal,
                    operation.Sequence,
                    verificationAuthorityFailure,
                    mutationObserved: false,
                    verifiedSteps).ConfigureAwait(false);
            }

            FolderSnapshot after = await snapshotService.CaptureAsync(
                request.Root,
                verificationAuthority.State.Grant,
                CancellationToken.None).ConfigureAwait(false);
            ExecutionStepVerification verification = ExecutionStepVerifier.Verify(
                before,
                after,
                operation,
                mutation.Evidence);
            if (!verification.IsSuccess)
            {
                return await FailAfterIntentAsync(
                    request,
                    journal,
                    operation.Sequence,
                    verification.ReasonCode,
                    mutationObserved: false,
                    verifiedSteps).ConfigureAwait(false);
            }

            JournalAppendResult verified = await AppendAsync(
                journal,
                (sequence, occurredUtc) => new StepVerifiedEvent(
                    journal.ExecutionId,
                    sequence,
                    occurredUtc,
                    operation.Sequence),
                CancellationToken.None).ConfigureAwait(false);
            if (!verified.IsSuccess)
            {
                return Result(
                    request,
                    FileExecutionStatus.PersistenceFailed,
                    verified.ReasonCode,
                    journal,
                    operation.Sequence,
                    verifiedSteps: verifiedSteps);
            }

            journal = verified.Journal!;
            verifiedSteps.Add(verification.Evidence!);
            Reach(request, FileExecutionBoundary.StepVerifiedPersisted, operation.Sequence);
        }

        DateTimeOffset completedUtc = clock.UtcNow;
        if (!IsValidUtc(completedUtc) || completedUtc < journal.Events[^1].OccurredUtc)
        {
            return Result(
                request,
                FileExecutionStatus.VerificationFailed,
                "execution.completion_time_invalid",
                journal,
                verifiedSteps: verifiedSteps);
        }

        DateTimeOffset? undoAvailableUntilUtc;
        try
        {
            undoAvailableUntilUtc = request.UndoWindow is null
                ? null
                : completedUtc + request.UndoWindow.Value;
        }
        catch (ArgumentOutOfRangeException)
        {
            return Result(
                request,
                FileExecutionStatus.VerificationFailed,
                "execution.undo_window_invalid",
                journal,
                verifiedSteps: verifiedSteps);
        }

        DomainResult<ExecutionReceipt> receiptResult = ExecutionReceipt.CreateVerified(
            request.ReceiptId,
            request.Plan,
            journal,
            completedUtc,
            undoAvailableUntilUtc,
            verifiedSteps);
        if (!receiptResult.IsSuccess)
        {
            return Result(
                request,
                FileExecutionStatus.VerificationFailed,
                receiptResult.Error!.Code,
                journal,
                verifiedSteps: verifiedSteps);
        }

        ExecutionReceipt receipt = receiptResult.Value!;
        JournalWriteResult storedReceipt = await journalStore.StoreReceiptAsync(
            receipt,
            CancellationToken.None).ConfigureAwait(false);
        if (!storedReceipt.IsSuccess)
        {
            return Result(
                request,
                FileExecutionStatus.PersistenceFailed,
                storedReceipt.FailureCode ?? "execution.receipt_write_failed",
                journal,
                receipt: receipt,
                verifiedSteps: verifiedSteps);
        }

        Reach(request, FileExecutionBoundary.ReceiptPersisted);
        return Result(
            request,
            FileExecutionStatus.Verified,
            "execution.verified",
            journal,
            receipt: receipt,
            verifiedSteps: verifiedSteps);
    }

    private async Task<FileExecutionResult> FailAfterIntentAsync(
        FileExecutionRequest request,
        ExecutionJournal journal,
        int stepSequence,
        string failureCode,
        bool mutationObserved,
        IReadOnlyCollection<VerifiedStepEvidence> verifiedSteps)
    {
        if (mutationObserved)
        {
            JournalAppendResult observed = await AppendAsync(
                journal,
                (sequence, occurredUtc) => new StepMutationObservedEvent(
                    journal.ExecutionId,
                    sequence,
                    occurredUtc,
                    stepSequence),
                CancellationToken.None).ConfigureAwait(false);
            if (!observed.IsSuccess)
            {
                return Result(
                    request,
                    FileExecutionStatus.PersistenceFailed,
                    observed.ReasonCode,
                    journal,
                    stepSequence,
                    verifiedSteps: verifiedSteps);
            }

            journal = observed.Journal!;
            Reach(request, FileExecutionBoundary.MutationObservedPersisted, stepSequence);
        }

        JournalAppendResult failed = await AppendAsync(
            journal,
            (sequence, occurredUtc) => new StepFailedEvent(
                journal.ExecutionId,
                sequence,
                occurredUtc,
                stepSequence,
                NormalizeReasonCode(failureCode)),
            CancellationToken.None).ConfigureAwait(false);
        if (!failed.IsSuccess)
        {
            return Result(
                request,
                FileExecutionStatus.PersistenceFailed,
                failed.ReasonCode,
                journal,
                stepSequence,
                verifiedSteps: verifiedSteps);
        }

        journal = failed.Journal!;
        Reach(request, FileExecutionBoundary.StepFailedPersisted, stepSequence);
        JournalAppendResult recovery = await AppendAsync(
            journal,
            (sequence, occurredUtc) => new StepRecoveryRequiredEvent(
                journal.ExecutionId,
                sequence,
                occurredUtc,
                stepSequence,
                "execution.inspect_before_recovery"),
            CancellationToken.None).ConfigureAwait(false);
        if (!recovery.IsSuccess)
        {
            return Result(
                request,
                FileExecutionStatus.PersistenceFailed,
                recovery.ReasonCode,
                journal,
                stepSequence,
                verifiedSteps: verifiedSteps);
        }

        journal = recovery.Journal!;
        Reach(request, FileExecutionBoundary.RecoveryRequiredPersisted, stepSequence);
        return Result(
            request,
            FileExecutionStatus.RecoveryRequired,
            failureCode,
            journal,
            stepSequence,
            verifiedSteps: verifiedSteps);
    }

    private async ValueTask<AuthorityCheck> ReadAndValidateAuthorityAsync(
        FileExecutionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            ExecutionAuthorityState? state = await authoritySource.ReadCurrentAsync(
                request.Plan.Definition.SkillId,
                request.Plan.Definition.SkillVersion,
                request.Plan.Definition.GrantId,
                cancellationToken).ConfigureAwait(false);
            if (state is null)
            {
                return AuthorityCheck.Failure("permission.authority_unavailable");
            }

            DomainResult<ExecutionAuthorization> checkedAuthority = permissionGateway.Revalidate(
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
            return JournalAppendResult.Failure("execution.journal_time_invalid");
        }

        ExecutionJournalEvent journalEvent;
        try
        {
            journalEvent = eventFactory(journal.Events.Count + 1L, occurredUtc);
        }
        catch (ArgumentException)
        {
            return JournalAppendResult.Failure("execution.journal_event_invalid");
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
                persisted.FailureCode ?? "execution.journal_write_failed");
    }

    private static string? ValidateVerificationAuthority(
        ExecutionPlan plan,
        LocalFolderGrant grant,
        DateTimeOffset nowUtc)
    {
        if (!grant.Allows(GrantCapability.Enumerate, nowUtc) ||
            !grant.Allows(GrantCapability.ReadMetadata, nowUtc))
        {
            return "permission.verification_not_granted";
        }

        bool requiresHash = plan.Definition.Operations.Any(
            static operation => operation.SourceFingerprint?.ContentHash is not null);
        return requiresHash && !grant.Allows(GrantCapability.ReadContentHash, nowUtc)
            ? "permission.hash_verification_not_granted"
            : null;
    }

    private static bool IsExpectedPrimitiveFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException;

    private static string PrimitiveFailureCode(Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException or SecurityException => "execution.primitive_access_denied",
            NotSupportedException => "execution.primitive_not_supported",
            _ => "execution.primitive_io_failure",
        };

    private static string PrimitiveFailureCode(FileMutationFailureKind failureKind) =>
        failureKind switch
        {
            FileMutationFailureKind.UnsupportedPlatform => "execution.primitive_not_supported",
            FileMutationFailureKind.InvalidRequest => "execution.primitive_not_allowed",
            FileMutationFailureKind.RootChanged => "execution.root_changed",
            FileMutationFailureKind.PathChanged => "execution.atomic_path_changed",
            FileMutationFailureKind.SourceMissing => "execution.source_missing",
            FileMutationFailureKind.SourceChanged => "execution.source_identity_changed",
            FileMutationFailureKind.DestinationExists => "execution.destination_exists",
            FileMutationFailureKind.AccessDenied => "execution.primitive_access_denied",
            FileMutationFailureKind.DirectoryNotEmpty => "execution.directory_not_empty",
            FileMutationFailureKind.LimitExceeded => "execution.source_file_limit_exceeded",
            FileMutationFailureKind.CleanupFailed => "execution.primitive_cleanup_failed",
            _ => "execution.primitive_io_failure",
        };

    private static string NormalizeReasonCode(string reasonCode) =>
        !string.IsNullOrWhiteSpace(reasonCode) &&
        reasonCode.Length <= 128 &&
        reasonCode.All(static value =>
            value is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')
            ? reasonCode
            : "execution.failure";

    private void Reach(
        FileExecutionRequest request,
        FileExecutionBoundary boundary,
        int? stepSequence = null) =>
        faultInjector.Reach(
            new FileExecutionBoundaryContext(
                request.ExecutionId,
                request.Mode,
                boundary,
                stepSequence));

    private bool DurationExceeded(DateTimeOffset startedUtc)
    {
        DateTimeOffset nowUtc = clock.UtcNow;
        return !IsValidUtc(nowUtc) || nowUtc < startedUtc || nowUtc - startedUtc > limits.MaximumDuration;
    }

    private static bool IsValidUtc(DateTimeOffset value) =>
        value.Offset == TimeSpan.Zero;

    private static FileExecutionResult Result(
        FileExecutionRequest request,
        FileExecutionStatus status,
        string reasonCode,
        ExecutionJournal? journal = null,
        int? failedStepSequence = null,
        ExecutionReceipt? receipt = null,
        IEnumerable<VerifiedStepEvidence>? verifiedSteps = null) =>
        new(
            status,
            reasonCode,
            request.Mode,
            journal,
            receipt,
            failedStepSequence,
            verifiedSteps ?? []);

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
            new(false, true, "execution.cancelled", null);
    }

    private sealed record JournalAppendResult(
        bool IsSuccess,
        string ReasonCode,
        ExecutionJournal? Journal)
    {
        public static JournalAppendResult Success(ExecutionJournal journal) =>
            new(true, "execution.journal_appended", journal);

        public static JournalAppendResult Failure(string reasonCode) =>
            new(false, reasonCode, null);
    }
}
