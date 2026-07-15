using Tooltail.Application.Abstractions;
using Tooltail.Contracts.Skills;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Execution;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Features.FileSkills.Planning;
using Tooltail.Features.FileSkills.Skills;
using Tooltail.Features.FileSkills.Snapshots;

namespace Tooltail.Features.FileSkills.Rehearsal;

public enum SkillRehearsalStatus
{
    Passed,
    InvalidRequest,
    SourceSnapshotFailed,
    WorkspaceFailed,
    StagingFailed,
    PlanningFailed,
    ExecutionFailed,
    CleanupRequired,
    Cancelled,
}

public sealed record SkillRehearsalRequest
{
    public SkillRehearsalRequest(
        SkillSpecContract specification,
        SkillVersion skillVersion,
        CanonicalLocalRoot sourceRoot,
        LocalFolderGrant sourceGrant,
        GrantId temporaryGrantId,
        PlanId planId,
        ApprovalId approvalId,
        ExecutionId executionId,
        ReceiptId receiptId,
        TimeSpan planLifetime,
        IReadOnlyDictionary<string, string>? userParameters = null)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(skillVersion);
        ArgumentNullException.ThrowIfNull(sourceRoot);
        ArgumentNullException.ThrowIfNull(sourceGrant);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(planLifetime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(planLifetime, TimeSpan.FromMinutes(30));
        Specification = specification;
        SkillVersion = skillVersion;
        SourceRoot = sourceRoot;
        SourceGrant = sourceGrant;
        TemporaryGrantId = temporaryGrantId;
        PlanId = planId;
        ApprovalId = approvalId;
        ExecutionId = executionId;
        ReceiptId = receiptId;
        PlanLifetime = planLifetime;
        UserParameters = userParameters;
    }

    public SkillSpecContract Specification { get; }

    public SkillVersion SkillVersion { get; }

    public CanonicalLocalRoot SourceRoot { get; }

    public LocalFolderGrant SourceGrant { get; }

    public GrantId TemporaryGrantId { get; }

    public PlanId PlanId { get; }

    public ApprovalId ApprovalId { get; }

    public ExecutionId ExecutionId { get; }

    public ReceiptId ReceiptId { get; }

    public TimeSpan PlanLifetime { get; }

    public IReadOnlyDictionary<string, string>? UserParameters { get; }
}

public sealed record SkillRehearsalResult
{
    internal SkillRehearsalResult(
        SkillRehearsalStatus status,
        string reasonCode,
        SkillId skillId,
        SkillVersionNumber skillVersion,
        SkillSpecificationHash specificationHash,
        DateTimeOffset startedUtc,
        DateTimeOffset completedUtc,
        PlanFingerprint? planFingerprint,
        FileExecutionResult? execution,
        RehearsalCleanupResult? cleanup)
    {
        Status = status;
        ReasonCode = reasonCode;
        SkillId = skillId;
        SkillVersion = skillVersion;
        SpecificationHash = specificationHash;
        StartedUtc = startedUtc;
        CompletedUtc = completedUtc;
        PlanFingerprint = planFingerprint;
        Execution = execution;
        Cleanup = cleanup;
    }

    public SkillRehearsalStatus Status { get; }

    public string ReasonCode { get; }

    public SkillId SkillId { get; }

    public SkillVersionNumber SkillVersion { get; }

    public SkillSpecificationHash SpecificationHash { get; }

    public DateTimeOffset StartedUtc { get; }

    public DateTimeOffset CompletedUtc { get; }

    public PlanFingerprint? PlanFingerprint { get; }

    public FileExecutionResult? Execution { get; }

    public RehearsalCleanupResult? Cleanup { get; }

    public bool IsPassed =>
        Status == SkillRehearsalStatus.Passed &&
        Execution?.IsVerified == true &&
        Cleanup?.IsSuccess == true;
}

public sealed class SkillRehearsalService
{
    private readonly IClock clock;
    private readonly IRehearsalWorkspaceFactory workspaceFactory;
    private readonly IExecutionJournalStore journalStore;
    private readonly WindowsPathSafetyService pathSafety;
    private readonly FolderSnapshotService snapshotService;
    private readonly SkillPlanner planner;
    private readonly RehearsalFixtureStager stager;
    private readonly IFileExecutionFaultInjector faultInjector;

    public SkillRehearsalService(
        IClock clock,
        IRehearsalWorkspaceFactory workspaceFactory,
        IExecutionJournalStore journalStore,
        WindowsPathSafetyService pathSafety,
        FolderSnapshotService snapshotService,
        SkillPlanner? planner = null,
        RehearsalFixtureLimits? fixtureLimits = null,
        IFileExecutionFaultInjector? faultInjector = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(workspaceFactory);
        ArgumentNullException.ThrowIfNull(journalStore);
        ArgumentNullException.ThrowIfNull(pathSafety);
        ArgumentNullException.ThrowIfNull(snapshotService);
        this.clock = clock;
        this.workspaceFactory = workspaceFactory;
        this.journalStore = journalStore;
        this.pathSafety = pathSafety;
        this.snapshotService = snapshotService;
        this.planner = planner ?? new SkillPlanner();
        stager = new RehearsalFixtureStager(pathSafety, fixtureLimits);
        this.faultInjector = faultInjector ?? NoFileExecutionFaultInjector.Instance;
    }

    public async Task<SkillRehearsalResult> RehearseAsync(
        SkillRehearsalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        DateTimeOffset startedUtc = clock.UtcNow;
        SkillSpecificationHash specificationHash;
        try
        {
            specificationHash = CanonicalSkillSpec.ComputeHash(request.Specification);
        }
        catch (ArgumentException)
        {
            return Result(
                request,
                new SkillSpecificationHash(new string('0', 64)),
                startedUtc,
                SkillRehearsalStatus.InvalidRequest,
                "rehearsal.skill_invalid");
        }

        string? requestFailure = ValidateRequest(request, specificationHash, startedUtc);
        requestFailure ??= RootsAreDisjoint(
            request.SourceRoot.CanonicalPath,
            workspaceFactory.OwnedTemporaryRoot.CanonicalPath)
            ? null
            : "rehearsal.workspace_overlaps_source";
        if (requestFailure is not null)
        {
            return Result(
                request,
                specificationHash,
                startedUtc,
                requestFailure == "rehearsal.cancelled"
                    ? SkillRehearsalStatus.Cancelled
                    : SkillRehearsalStatus.InvalidRequest,
                requestFailure);
        }

        FolderSnapshot sourceSnapshot = await snapshotService.CaptureAsync(
            request.SourceRoot,
            request.SourceGrant,
            cancellationToken).ConfigureAwait(false);
        if (!sourceSnapshot.IsComplete)
        {
            return Result(
                request,
                specificationHash,
                startedUtc,
                cancellationToken.IsCancellationRequested
                    ? SkillRehearsalStatus.Cancelled
                    : SkillRehearsalStatus.SourceSnapshotFailed,
                sourceSnapshot.ReasonCode ?? "rehearsal.source_snapshot_failed");
        }

        RehearsalWorkspaceResult workspaceResult = await workspaceFactory.CreateAsync(
            cancellationToken).ConfigureAwait(false);
        if (!workspaceResult.IsSuccess)
        {
            return Result(
                request,
                specificationHash,
                startedUtc,
                workspaceResult.ReasonCode == "rehearsal.cancelled"
                    ? SkillRehearsalStatus.Cancelled
                    : SkillRehearsalStatus.WorkspaceFailed,
                workspaceResult.ReasonCode);
        }

        RehearsalWorkspace workspace = workspaceResult.Workspace!;
        RehearsalRunOutcome run;
        if (!RootsAreDisjoint(request.SourceRoot.CanonicalPath, workspace.Root.CanonicalPath))
        {
            run = RehearsalRunOutcome.Failure(
                SkillRehearsalStatus.InvalidRequest,
                "rehearsal.workspace_overlaps_source");
        }
        else
        {
            run = await RunInWorkspaceAsync(
                request,
                specificationHash,
                sourceSnapshot,
                workspace,
                cancellationToken).ConfigureAwait(false);
        }

        RehearsalCleanupResult cleanup = await workspaceFactory.CleanupAsync(
            workspace,
            CancellationToken.None).ConfigureAwait(false);
        SkillRehearsalStatus finalStatus = cleanup.IsSuccess
            ? run.Status
            : SkillRehearsalStatus.CleanupRequired;
        string finalReason = cleanup.IsSuccess ? run.ReasonCode : cleanup.ReasonCode;
        return Result(
            request,
            specificationHash,
            startedUtc,
            finalStatus,
            finalReason,
            run.PlanFingerprint,
            run.Execution,
            cleanup);
    }

    private async Task<RehearsalRunOutcome> RunInWorkspaceAsync(
        SkillRehearsalRequest request,
        SkillSpecificationHash specificationHash,
        FolderSnapshot sourceSnapshot,
        RehearsalWorkspace workspace,
        CancellationToken cancellationToken)
    {
        DateTimeOffset grantIssuedUtc = clock.UtcNow;
        LocalFolderGrant temporaryGrant = LocalFolderGrant.Issue(
            request.TemporaryGrantId,
            request.SourceGrant.CompanionId,
            workspace.Root.Identity,
            request.SourceGrant.Capabilities,
            grantIssuedUtc,
            grantIssuedUtc + request.PlanLifetime + TimeSpan.FromMinutes(5));
        RehearsalStagingResult staging = await stager.StageAsync(
            request.SourceRoot,
            sourceSnapshot,
            workspace,
            cancellationToken).ConfigureAwait(false);
        if (!staging.IsSuccess)
        {
            return RehearsalRunOutcome.Failure(
                staging.ReasonCode == "rehearsal.cancelled"
                    ? SkillRehearsalStatus.Cancelled
                    : SkillRehearsalStatus.StagingFailed,
                staging.ReasonCode);
        }

        FolderSnapshot sourceAfterStaging = await snapshotService.CaptureAsync(
            request.SourceRoot,
            request.SourceGrant,
            cancellationToken).ConfigureAwait(false);
        FolderSnapshot stagedSnapshot = await snapshotService.CaptureAsync(
            workspace.Root,
            temporaryGrant,
            cancellationToken).ConfigureAwait(false);
        if (!sourceAfterStaging.IsComplete || !stagedSnapshot.IsComplete)
        {
            return RehearsalRunOutcome.Failure(
                cancellationToken.IsCancellationRequested
                    ? SkillRehearsalStatus.Cancelled
                    : SkillRehearsalStatus.StagingFailed,
                sourceAfterStaging.ReasonCode ??
                stagedSnapshot.ReasonCode ??
                "rehearsal.staging_snapshot_failed");
        }

        if (!sourceSnapshot.Entries.SequenceEqual(sourceAfterStaging.Entries))
        {
            return RehearsalRunOutcome.Failure(
                SkillRehearsalStatus.StagingFailed,
                "rehearsal.source_changed_during_staging");
        }

        if (!FixtureTreesEquivalent(sourceSnapshot, stagedSnapshot))
        {
            return RehearsalRunOutcome.Failure(
                SkillRehearsalStatus.StagingFailed,
                "rehearsal.staged_fixture_mismatch");
        }

        SkillSpecContract reboundSpecification = request.Specification with
        {
            Applicability = request.Specification.Applicability with
            {
                RootGrantId = temporaryGrant.Id.Value,
            },
        };
        SkillSpecificationHash reboundHash = CanonicalSkillSpec.ComputeHash(reboundSpecification);
        DateTimeOffset planCreatedUtc = clock.UtcNow;
        SkillPlanningResult planning = planner.DryRun(
            new SkillPlanningRequest(
                request.PlanId,
                reboundSpecification,
                reboundHash,
                temporaryGrant,
                stagedSnapshot,
                planCreatedUtc,
                planCreatedUtc + request.PlanLifetime,
                request.UserParameters));
        if (planning.Status != SkillPlanningStatus.Ready)
        {
            return RehearsalRunOutcome.Failure(
                SkillRehearsalStatus.PlanningFailed,
                planning.Diagnostics.Count > 0
                    ? planning.Diagnostics[0].Code
                    : "rehearsal.planning_failed");
        }

        ExecutionPlan temporaryPlan = BindPlanToExactSkillVersion(
            planning.Plan!,
            specificationHash);
        DateTimeOffset approvedUtc = clock.UtcNow;
        PlanApproval rehearsalApproval = PlanApproval.IssueRehearsal(
            request.ApprovalId,
            temporaryPlan,
            approvedUtc,
            temporaryPlan.Definition.ExpiresUtc);
        PermissionGateway gateway = new(clock);
        var authorized = gateway.AuthorizeRehearsal(
            temporaryPlan,
            request.SkillVersion,
            temporaryGrant,
            rehearsalApproval);
        if (!authorized.IsSuccess)
        {
            return RehearsalRunOutcome.Failure(
                SkillRehearsalStatus.ExecutionFailed,
                authorized.Error!.Code,
                temporaryPlan.Fingerprint);
        }

        FileSkillExecutor executor = new(
            clock,
            new FixedRehearsalAuthoritySource(
                request.SkillVersion,
                temporaryGrant),
            journalStore,
            pathSafety,
            snapshotService,
            faultInjector);
        FileExecutionResult execution = await executor.ExecuteAsync(
            new FileExecutionRequest(
                request.ExecutionId,
                request.ReceiptId,
                temporaryPlan,
                authorized.Value!,
                workspace.Root,
                FileExecutionMode.Rehearsal),
            cancellationToken).ConfigureAwait(false);
        return execution.IsVerified
            ? RehearsalRunOutcome.Passed(temporaryPlan.Fingerprint, execution)
            : RehearsalRunOutcome.Failure(
                execution.Status == FileExecutionStatus.Cancelled
                    ? SkillRehearsalStatus.Cancelled
                    : SkillRehearsalStatus.ExecutionFailed,
                execution.ReasonCode,
                temporaryPlan.Fingerprint,
                execution);
    }

    private static string? ValidateRequest(
        SkillRehearsalRequest request,
        SkillSpecificationHash specificationHash,
        DateTimeOffset nowUtc)
    {
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            return "rehearsal.non_utc_time";
        }

        if (request.SkillVersion.SkillId.Value != request.Specification.SkillId ||
            request.SkillVersion.Number.Value != request.Specification.Version ||
            !string.Equals(
                request.SkillVersion.SpecificationHash,
                specificationHash.Value,
                StringComparison.Ordinal) ||
            request.SkillVersion.Lifecycle == SkillLifecycleState.Stale)
        {
            return "rehearsal.skill_version_mismatch";
        }

        if (request.SourceRoot.Identity != request.SourceGrant.RootIdentity ||
            request.Specification.Applicability.RootGrantId != request.SourceGrant.Id.Value)
        {
            return "rehearsal.source_authority_mismatch";
        }

        if (!request.SourceGrant.Allows(GrantCapability.Enumerate, nowUtc) ||
            !request.SourceGrant.Allows(GrantCapability.ReadMetadata, nowUtc) ||
            !request.SourceGrant.Allows(GrantCapability.ReadContentHash, nowUtc))
        {
            return "rehearsal.source_authority_inactive";
        }

        return null;
    }

    private static ExecutionPlan BindPlanToExactSkillVersion(
        ExecutionPlan rehearsalPlan,
        SkillSpecificationHash exactSpecificationHash)
    {
        ExecutionPlanDefinition staged = rehearsalPlan.Definition;
        ExecutionPlanDefinition exact = new(
            staged.Id,
            staged.SkillId,
            staged.SkillVersion,
            exactSpecificationHash,
            staged.GrantId,
            staged.RootIdentity,
            staged.GrantedCapabilities,
            staged.CreatedUtc,
            staged.ExpiresUtc,
            staged.Operations);
        return CanonicalExecutionPlan.Create(exact).Value!;
    }

    private static bool FixtureTreesEquivalent(
        FolderSnapshot source,
        FolderSnapshot staged)
    {
        if (source.Entries.Count != staged.Entries.Count)
        {
            return false;
        }

        Dictionary<string, FolderSnapshotEntry> stagedByPath = staged.Entries.ToDictionary(
            static entry => entry.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        foreach (FolderSnapshotEntry expected in source.Entries)
        {
            if (!stagedByPath.TryGetValue(expected.RelativePath, out FolderSnapshotEntry? actual) ||
                !string.Equals(expected.RelativePath, actual.RelativePath, StringComparison.Ordinal) ||
                expected.Kind != actual.Kind ||
                expected.IsReparsePoint != actual.IsReparsePoint ||
                expected.Attributes != actual.Attributes)
            {
                return false;
            }

            if (expected.Kind == SnapshotEntryKind.File &&
                (expected.Length != actual.Length ||
                 expected.CreationUtc != actual.CreationUtc ||
                 expected.LastWriteUtc != actual.LastWriteUtc ||
                 expected.ContentHashStatus != actual.ContentHashStatus ||
                 expected.ContentHash != actual.ContentHash))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RootsAreDisjoint(string left, string right) =>
        !IsSameOrDescendant(left, right) && !IsSameOrDescendant(right, left);

    private static bool IsSameOrDescendant(string root, string candidate)
    {
        string relative = Path.GetRelativePath(root, candidate);
        return relative == "." ||
            (relative != ".." &&
             !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
             !Path.IsPathRooted(relative));
    }

    private SkillRehearsalResult Result(
        SkillRehearsalRequest request,
        SkillSpecificationHash specificationHash,
        DateTimeOffset startedUtc,
        SkillRehearsalStatus status,
        string reasonCode,
        PlanFingerprint? planFingerprint = null,
        FileExecutionResult? execution = null,
        RehearsalCleanupResult? cleanup = null)
    {
        DateTimeOffset completedUtc = clock.UtcNow;
        if (completedUtc.Offset != TimeSpan.Zero || completedUtc < startedUtc)
        {
            completedUtc = startedUtc;
        }

        return new SkillRehearsalResult(
            status,
            reasonCode,
            request.SkillVersion.SkillId,
            request.SkillVersion.Number,
            specificationHash,
            startedUtc,
            completedUtc,
            planFingerprint,
            execution,
            cleanup);
    }

    private sealed class FixedRehearsalAuthoritySource(
        SkillVersion skillVersion,
        LocalFolderGrant grant) : IExecutionAuthoritySource
    {
        public ValueTask<ExecutionAuthorityState?> ReadCurrentAsync(
            SkillId skillId,
            SkillVersionNumber requestedVersion,
            GrantId grantId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ExecutionAuthorityState?>(
                new ExecutionAuthorityState(skillVersion, grant));
        }
    }

    private sealed record RehearsalRunOutcome(
        SkillRehearsalStatus Status,
        string ReasonCode,
        PlanFingerprint? PlanFingerprint,
        FileExecutionResult? Execution)
    {
        public static RehearsalRunOutcome Passed(
            PlanFingerprint planFingerprint,
            FileExecutionResult execution) =>
            new(
                SkillRehearsalStatus.Passed,
                "rehearsal.passed",
                planFingerprint,
                execution);

        public static RehearsalRunOutcome Failure(
            SkillRehearsalStatus status,
            string reasonCode,
            PlanFingerprint? planFingerprint = null,
            FileExecutionResult? execution = null) =>
            new(status, reasonCode, planFingerprint, execution);
    }
}
