using System.Security.Cryptography;
using System.Text;
using Tooltail.Application.Abstractions;
using Tooltail.Contracts.Capsules;
using Tooltail.Contracts.Json;
using Tooltail.Contracts.Skills;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Compilation;
using Tooltail.Features.FileSkills.Continuity;
using Tooltail.Features.FileSkills.Execution;
using Tooltail.Features.FileSkills.Observation;
using Tooltail.Features.FileSkills.Planning;
using Tooltail.Features.FileSkills.Reconciliation;
using Tooltail.Features.FileSkills.Rehearsal;
using Tooltail.Features.FileSkills.Skills;
using Tooltail.Features.FileSkills.Snapshots;
using Tooltail.Features.FileSkills.Undo;

namespace Tooltail.SkillFixtureCli;

internal sealed record FixturePipelineResult(
    int ExitCode,
    string ReasonCode,
    object? Data)
{
    public bool IsSuccess => ExitCode == 0;

    public static FixturePipelineResult Success(string reasonCode, object? data = null) =>
        new(0, reasonCode, data);

    public static FixturePipelineResult Failure(
        string reasonCode,
        object? data = null,
        int exitCode = 1) =>
        new(exitCode, reasonCode, data);
}

internal sealed record FixtureSnapshotOutput(
    string Phase,
    FolderSnapshotStatus Status,
    int EntryCount,
    long HashedBytes,
    IReadOnlyList<FixtureTreeEntry> Tree);

internal sealed record FixtureReconciliationOutput(
    SnapshotReconciliationStatus Status,
    int EffectCount,
    IReadOnlyList<FixtureEffectDocument> Effects);

internal sealed record FixtureCandidateOutput(
    string Key,
    string SpecificationSha256,
    string Summary);

internal sealed record FixtureCompileOutput(
    SkillCompilationStatus Status,
    IReadOnlyList<FixtureCandidateOutput> Candidates,
    IReadOnlyList<CompilerQuestion> Questions,
    SkillSpecContract? Specification,
    string? SpecificationSha256);

internal sealed record FixturePlanOperation(
    int Sequence,
    FilePrimitive Primitive,
    string? SourceRelativePath,
    string DestinationRelativePath,
    DestinationPrecondition DestinationPrecondition,
    ExpectedSourceState ExpectedSourceState,
    ExpectedDestinationState ExpectedDestinationState);

internal sealed record FixturePlanOutput(
    Guid PlanId,
    string PlanFingerprint,
    int MatchedFileCount,
    IReadOnlyList<FixturePlanOperation> Operations);

internal sealed record FixtureRehearsalOutput(
    SkillRehearsalStatus Status,
    string? PlanFingerprint,
    FixtureReceiptProjection? Receipt,
    string CleanupReasonCode);

internal sealed record FixtureExecutionOutput(
    FileExecutionStatus Status,
    FixtureReceiptProjection? Receipt,
    IReadOnlyList<FixtureTreeEntry> FinalTree);

internal sealed record FixtureVerificationOutput(
    FixtureReceiptProjection Receipt,
    IReadOnlyList<StepRecoveryStatus> StepStatuses,
    IReadOnlyList<FixtureTreeEntry> CurrentTree);

internal sealed record FixtureUndoOutput(
    UndoExecutionStatus Status,
    string RecoveryPlanFingerprint,
    FixtureRecoveryReceiptProjection? Receipt,
    bool RestoredOriginalTree,
    IReadOnlyList<FixtureTreeEntry> RestoredTree);

internal sealed record FixtureCapsuleOutput(
    Guid CapsuleId,
    string CapsuleSha256,
    int SkillCount,
    CompanionCapsuleContract Capsule);

internal static class FixturePipeline
{
    private const string BaselineSnapshotName = "baseline.snapshot.json";
    private const string FinalSnapshotName = "final.snapshot.json";
    private const string PlanningSnapshotName = "planning.snapshot.json";
    private const string ReconciliationName = "reconciliation.json";
    private const string SkillSpecName = "skill-spec.json";
    private const string PlanName = "plan.json";
    private const string RehearsalName = "rehearsal.json";
    private const string ExecutionName = "execution.json";
    private const string ExecutionFinalSnapshotName = "execution-final.snapshot.json";
    private const string RecoveryPlanName = "recovery-plan.json";
    private const string UndoName = "undo.json";
    private const string CapsuleName = "companion-capsule.json";

    public static async Task<FixturePipelineResult> SnapshotAsync(
        FixtureWorkspace workspace,
        string phase,
        CancellationToken cancellationToken = default)
    {
        (string ArtifactName, int ClockMinute)? phaseData = phase switch
        {
            "baseline" => (BaselineSnapshotName, 1),
            "final" => (FinalSnapshotName, 2),
            "planning" => (PlanningSnapshotName, 4),
            _ => null,
        };
        if (phaseData is null)
        {
            return FixturePipelineResult.Failure(
                "fixture.snapshot_phase_invalid",
                exitCode: 2);
        }

        FixtureValueResult<FixtureRuntime> opened = await FixtureRuntime.CreateAsync(
            workspace,
            phaseData.Value.ClockMinute,
            cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return FixturePipelineResult.Failure(opened.ReasonCode);
        }

        FixtureRuntime runtime = opened.Value!;
        FolderSnapshot snapshot = await runtime.SnapshotService.CaptureAsync(
            runtime.Root,
            runtime.Grant,
            cancellationToken).ConfigureAwait(false);
        await workspace.WriteArtifactAsync(
            phaseData.Value.ArtifactName,
            FixtureArtifacts.EncodeSnapshot(snapshot),
            cancellationToken).ConfigureAwait(false);
        StateWriteResult stored = await runtime.StoreSnapshotAsync(
            phase,
            snapshot,
            cancellationToken).ConfigureAwait(false);
        if (!stored.IsSuccess)
        {
            return FixturePipelineResult.Failure(stored.FailureCode!);
        }

        FixtureSnapshotOutput data = new(
            phase,
            snapshot.Status,
            snapshot.Entries.Count,
            snapshot.HashedBytes,
            FixtureArtifacts.Tree(snapshot));
        return snapshot.IsComplete
            ? FixturePipelineResult.Success("fixture.snapshot_complete", data)
            : FixturePipelineResult.Failure(snapshot.ReasonCode!, data);
    }

    public static async Task<FixturePipelineResult> ReconcileAsync(
        FixtureWorkspace workspace,
        bool watcherOverflow,
        IEnumerable<WatcherHint>? hints = null,
        CancellationToken cancellationToken = default)
    {
        FixtureValueResult<FolderSnapshot> baseline = await LoadSnapshotAsync(
            workspace,
            BaselineSnapshotName,
            cancellationToken).ConfigureAwait(false);
        FixtureValueResult<FolderSnapshot> final = await LoadSnapshotAsync(
            workspace,
            FinalSnapshotName,
            cancellationToken).ConfigureAwait(false);
        if (!baseline.IsSuccess || !final.IsSuccess)
        {
            return FixturePipelineResult.Failure(
                !baseline.IsSuccess ? baseline.ReasonCode : final.ReasonCode);
        }

        WatcherHint[] boundedHints = (hints ?? []).Take(10_001).ToArray();
        if (boundedHints.Length > 10_000)
        {
            return FixturePipelineResult.Failure("fixture.watcher_hint_limit_exceeded");
        }

        SnapshotReconciliation reconciliation = SnapshotReconciler.Reconcile(
            baseline.Value!,
            final.Value!,
            new WatcherHintBatch(
                boundedHints,
                overflowed: watcherOverflow,
                quiesced: true));
        byte[] artifact = FixtureArtifacts.EncodeReconciliation(reconciliation);
        await workspace.WriteArtifactAsync(
            ReconciliationName,
            artifact,
            cancellationToken).ConfigureAwait(false);
        FixtureValueResult<FixtureRuntime> opened = await FixtureRuntime.CreateAsync(
            workspace,
            clockMinute: 2,
            cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return FixturePipelineResult.Failure(opened.ReasonCode);
        }

        StateWriteResult stored = await opened.Value!.StoreEpisodeAsync(
            reconciliation,
            cancellationToken).ConfigureAwait(false);
        if (!stored.IsSuccess)
        {
            return FixturePipelineResult.Failure(stored.FailureCode!);
        }

        FixtureReconciliationOutput data = new(
            reconciliation.Status,
            reconciliation.Effects.Count,
            reconciliation.Effects.Select(static effect => new FixtureEffectDocument
            {
                Kind = effect.Kind,
                SourceRelativePath = effect.SourceRelativePath,
                DestinationRelativePath = effect.DestinationRelativePath,
                ReasonCode = effect.ReasonCode,
                CandidateSourcePaths = effect.CandidateSourcePaths,
            }).ToArray());
        return reconciliation.IsCompilable
            ? FixturePipelineResult.Success(reconciliation.ReasonCode, data)
            : FixturePipelineResult.Failure(reconciliation.ReasonCode, data);
    }

    public static async Task<FixturePipelineResult> CompileAsync(
        FixtureWorkspace workspace,
        IReadOnlyList<SkillUserAnswerContract> answers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(answers);
        FixtureValueResult<(FixtureReconciliationDocument Document, byte[] Bytes)>
            storedReconciliation = await LoadReconciliationAsync(
                workspace,
                cancellationToken).ConfigureAwait(false);
        if (!storedReconciliation.IsSuccess)
        {
            return FixturePipelineResult.Failure(storedReconciliation.ReasonCode);
        }

        FixtureReconciliationDocument reconciliationDocument =
            storedReconciliation.Value.Document;
        if (reconciliationDocument.Status != SnapshotReconciliationStatus.Complete)
        {
            return FixturePipelineResult.Failure(
                CompilationBarrierReason(reconciliationDocument.Status));
        }

        FixtureValueResult<FolderSnapshot> baseline = await LoadSnapshotAsync(
            workspace,
            BaselineSnapshotName,
            cancellationToken).ConfigureAwait(false);
        FixtureValueResult<FolderSnapshot> final = await LoadSnapshotAsync(
            workspace,
            FinalSnapshotName,
            cancellationToken).ConfigureAwait(false);
        if (!baseline.IsSuccess || !final.IsSuccess)
        {
            return FixturePipelineResult.Failure(
                !baseline.IsSuccess ? baseline.ReasonCode : final.ReasonCode);
        }

        SnapshotReconciliation reconciliation = SnapshotReconciler.Reconcile(
            baseline.Value!,
            final.Value!,
            WatcherHintBatch.Empty);
        byte[] canonicalReconciliation = FixtureArtifacts.EncodeReconciliation(reconciliation);
        if (!reconciliation.IsCompilable ||
            !storedReconciliation.Value.Bytes.AsSpan().SequenceEqual(canonicalReconciliation))
        {
            return FixturePipelineResult.Failure(
                reconciliation.IsCompilable
                    ? "fixture.reconciliation_artifact_mismatch"
                    : reconciliation.ReasonCode);
        }

        ReconciledFileEffect[] fileEffects = reconciliation.Effects
            .Where(static effect => effect.Kind is
                ReconciledEffectKind.Renamed or
                ReconciledEffectKind.Moved or
                ReconciledEffectKind.Copied)
            .ToArray();
        if (fileEffects.Length is < 2 or > 5)
        {
            return FixturePipelineResult.Failure("compiler.needs_two_examples");
        }

        FixtureValueResult<FixtureRuntime> opened = await FixtureRuntime.CreateAsync(
            workspace,
            clockMinute: 3,
            cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return FixturePipelineResult.Failure(opened.ReasonCode);
        }

        FixtureRuntime runtime = opened.Value!;
        TeachingFileExample[] examples = fileEffects.Select((effect, index) =>
            new TeachingFileExample(
                new ExampleId(workspace.Id($"example:{index + 1}")),
                new TeachingEpisodeId(workspace.Id("episode")),
                runtime.Grant.Id,
                runtime.Root.Identity,
                effect)).ToArray();
        SkillCompilationResult compilation = DeterministicSkillCompiler.Compile(
            new SkillCompilationRequest(
                new SkillId(workspace.Id("skill")),
                version: 1,
                workspace.Manifest.SkillName,
                workspace.Manifest.SkillDescription,
                runtime.Grant.Id,
                workspace.AtMinute(3),
                examples,
                exclusions: null,
                answers));
        FixtureCandidateOutput[] candidates = compilation.Candidates.Select(
            static candidate => new FixtureCandidateOutput(
                candidate.Key,
                CanonicalSkillSpec.ComputeHash(candidate.Specification).Value,
                candidate.Summary)).ToArray();
        SkillSpecContract? specification = compilation.SelectedCandidate?.Specification;
        string? hash = specification is null
            ? null
            : CanonicalSkillSpec.ComputeHash(specification).Value;
        FixtureCompileOutput data = new(
            compilation.Status,
            candidates,
            compilation.Questions,
            specification,
            hash);
        if (specification is null)
        {
            int exitCode = compilation.Status == SkillCompilationStatus.NeedsClarification
                ? 3
                : 1;
            return FixturePipelineResult.Failure(
                compilation.ReasonCode,
                data,
                exitCode);
        }

        byte[] canonical = CanonicalSkillSpec.Encode(specification);
        await workspace.WriteArtifactAsync(
            SkillSpecName,
            canonical,
            cancellationToken).ConfigureAwait(false);
        StateWriteResult stored = await runtime.StoreSkillAsync(
            specification,
            SkillLifecycleState.Draft,
            cancellationToken).ConfigureAwait(false);
        return stored.IsSuccess
            ? FixturePipelineResult.Success(compilation.ReasonCode, data)
            : FixturePipelineResult.Failure(stored.FailureCode!, data);
    }

    public static async Task<FixturePipelineResult> ValidateAsync(
        FixtureWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        FixtureValueResult<SkillSpecContract> specification = await LoadSpecificationAsync(
            workspace,
            cancellationToken).ConfigureAwait(false);
        if (!specification.IsSuccess)
        {
            return FixturePipelineResult.Failure(specification.ReasonCode);
        }

        SkillValidationResult validation = SkillSpecValidator.Validate(specification.Value!);
        object data = new
        {
            validation.IsValid,
            Errors = validation.Errors,
            SpecificationSha256 = validation.IsValid
                ? CanonicalSkillSpec.ComputeHash(specification.Value!).Value
                : null,
        };
        return validation.IsValid
            ? FixturePipelineResult.Success("fixture.skill_valid", data)
            : FixturePipelineResult.Failure("fixture.skill_invalid", data);
    }

    public static async Task<FixturePipelineResult> PlanAsync(
        FixtureWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        FixturePipelineResult snapshot = await SnapshotAsync(
            workspace,
            "planning",
            cancellationToken).ConfigureAwait(false);
        if (!snapshot.IsSuccess)
        {
            return snapshot;
        }

        FixtureValueResult<SkillSpecContract> specification = await LoadSpecificationAsync(
            workspace,
            cancellationToken).ConfigureAwait(false);
        FixtureValueResult<FolderSnapshot> planningSnapshot = await LoadSnapshotAsync(
            workspace,
            PlanningSnapshotName,
            cancellationToken).ConfigureAwait(false);
        if (!specification.IsSuccess || !planningSnapshot.IsSuccess)
        {
            return FixturePipelineResult.Failure(
                !specification.IsSuccess
                    ? specification.ReasonCode
                    : planningSnapshot.ReasonCode);
        }

        FixtureValueResult<FixtureRuntime> opened = await FixtureRuntime.CreateAsync(
            workspace,
            clockMinute: 5,
            cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return FixturePipelineResult.Failure(opened.ReasonCode);
        }

        FixtureRuntime runtime = opened.Value!;
        SkillPlanningResult planning = CreatePlan(
            workspace,
            runtime,
            specification.Value!,
            planningSnapshot.Value!);
        if (planning.Status != SkillPlanningStatus.Ready)
        {
            return FixturePipelineResult.Failure(
                planning.Diagnostics.Count > 0
                    ? planning.Diagnostics[0].Code
                    : "fixture.plan_failed",
                new { planning.Status, planning.MatchedFileCount, planning.Diagnostics });
        }

        ExecutionPlan plan = planning.Plan!;
        byte[] canonical = CanonicalExecutionPlan.Encode(plan.Definition);
        await workspace.WriteArtifactAsync(
            PlanName,
            canonical,
            cancellationToken).ConfigureAwait(false);
        StateWriteResult stored = await runtime.StateStore.StoreExecutionPlanAsync(
            plan,
            Encoding.UTF8.GetString(canonical),
            cancellationToken).ConfigureAwait(false);
        if (!stored.IsSuccess)
        {
            return FixturePipelineResult.Failure(stored.FailureCode!);
        }

        return FixturePipelineResult.Success(
            "fixture.plan_ready",
            Project(plan, planning.MatchedFileCount));
    }

    public static async Task<FixturePipelineResult> RehearseAsync(
        FixtureWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        FixtureValueResult<SkillSpecContract> specification = await LoadSpecificationAsync(
            workspace,
            cancellationToken).ConfigureAwait(false);
        if (!specification.IsSuccess)
        {
            return FixturePipelineResult.Failure(specification.ReasonCode);
        }

        FixtureValueResult<FixtureRuntime> opened = await FixtureRuntime.CreateAsync(
            workspace,
            clockMinute: 6,
            cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return FixturePipelineResult.Failure(opened.ReasonCode);
        }

        FixtureRuntime runtime = opened.Value!;
        SkillVersion draft = FixtureRuntime.SkillVersion(
            specification.Value!,
            SkillLifecycleState.Draft);
        TooltailOwnedRehearsalWorkspaceFactory workspaceFactory = new(
            runtime.TemporaryRoot,
            runtime.PathSafety,
            new FixtureSequenceIdGenerator(workspace.Manifest.WorkspaceId, "rehearsal"));
        SkillRehearsalService service = new(
            runtime.Clock,
            workspaceFactory,
            new InMemoryExecutionJournalStore(),
            new InMemoryRehearsalExecutionPersistence(),
            runtime.PathSafety,
            runtime.SnapshotService,
            runtime.MutationEngine);
        SkillRehearsalResult result = await service.RehearseAsync(
            new SkillRehearsalRequest(
                specification.Value!,
                draft,
                runtime.Root,
                runtime.Grant,
                new GrantId(workspace.Id("rehearsal:grant")),
                new PlanId(workspace.Id("rehearsal:plan")),
                new ApprovalId(workspace.Id("rehearsal:approval")),
                new ExecutionId(workspace.Id("rehearsal:execution")),
                new ReceiptId(workspace.Id("rehearsal:receipt")),
                TimeSpan.FromMinutes(20)),
            cancellationToken).ConfigureAwait(false);
        FixtureRehearsalOutput data = new(
            result.Status,
            result.PlanFingerprint?.Value,
            result.Execution?.Receipt is null
                ? null
                : FixtureArtifacts.Project(result.Execution.Receipt),
            result.Cleanup?.ReasonCode ?? "rehearsal.cleanup_not_started");
        await workspace.WriteArtifactAsync(
            RehearsalName,
            FixtureJson.Serialize(data),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsPassed)
        {
            return FixturePipelineResult.Failure(result.ReasonCode, data);
        }

        StateWriteResult stored = await runtime.StoreSkillAsync(
            specification.Value!,
            SkillLifecycleState.Approved,
            cancellationToken).ConfigureAwait(false);
        return stored.IsSuccess
            ? FixturePipelineResult.Success(result.ReasonCode, data)
            : FixturePipelineResult.Failure(stored.FailureCode!, data);
    }

    public static async Task<FixturePipelineResult> ExecuteAsync(
        FixtureWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        FixtureValueResult<SkillSpecContract> specification = await LoadSpecificationAsync(
            workspace,
            cancellationToken).ConfigureAwait(false);
        FixtureValueResult<FolderSnapshot> planningSnapshot = await LoadSnapshotAsync(
            workspace,
            PlanningSnapshotName,
            cancellationToken).ConfigureAwait(false);
        if (!specification.IsSuccess || !planningSnapshot.IsSuccess)
        {
            return FixturePipelineResult.Failure(
                !specification.IsSuccess
                    ? specification.ReasonCode
                    : planningSnapshot.ReasonCode);
        }

        FixtureValueResult<FixtureRuntime> opened = await FixtureRuntime.CreateAsync(
            workspace,
            clockMinute: 8,
            cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return FixturePipelineResult.Failure(opened.ReasonCode);
        }

        FixtureRuntime runtime = opened.Value!;
        FixtureValueResult<ExecutionPlan> planResult = await RecreatePlanAsync(
            workspace,
            runtime,
            specification.Value!,
            planningSnapshot.Value!,
            cancellationToken).ConfigureAwait(false);
        if (!planResult.IsSuccess)
        {
            return FixturePipelineResult.Failure(planResult.ReasonCode);
        }

        ExecutionPlan plan = planResult.Value!;
        StateReadResult<SkillVersionStateRecord> loadedSkill =
            await runtime.StateStore.LoadSkillVersionAsync(
                plan.Definition.SkillId,
                plan.Definition.SkillVersion,
                cancellationToken).ConfigureAwait(false);
        if (!loadedSkill.IsSuccess ||
            loadedSkill.Value!.Version.Lifecycle != SkillLifecycleState.Approved)
        {
            return FixturePipelineResult.Failure(
                loadedSkill.IsSuccess
                    ? "fixture.skill_not_approved"
                    : loadedSkill.ReasonCode);
        }

        PlanApproval approval = PlanApproval.Issue(
            new ApprovalId(workspace.Id("production:approval")),
            plan,
            workspace.AtMinute(7),
            plan.Definition.ExpiresUtc);
        StateWriteResult storedApproval = await runtime.StateStore.StoreApprovalAsync(
            approval,
            cancellationToken).ConfigureAwait(false);
        if (!storedApproval.IsSuccess)
        {
            return FixturePipelineResult.Failure(storedApproval.FailureCode!);
        }

        var authorized = new PermissionGateway(runtime.Clock).Authorize(
            plan,
            loadedSkill.Value.Version,
            runtime.Grant,
            approval);
        if (!authorized.IsSuccess)
        {
            return FixturePipelineResult.Failure(authorized.Error!.Code);
        }

        FileSkillExecutor executor = new(
            runtime.Clock,
            new FixtureAuthoritySource(loadedSkill.Value.Version, runtime.Grant),
            runtime.JournalStore,
            runtime.PathSafety,
            runtime.SnapshotService,
            runtime.MutationEngine,
            new FixtureMetadataStabilizer(
                runtime.Root,
                plan,
                workspace.AtMinute(0)));
        FileExecutionResult execution = await executor.ExecuteAsync(
            new FileExecutionRequest(
                new ExecutionId(workspace.Id("production:execution")),
                new ReceiptId(workspace.Id("production:receipt")),
                plan,
                authorized.Value!,
                runtime.Root,
                FileExecutionMode.Production,
                undoWindow: TimeSpan.FromMinutes(60)),
            cancellationToken).ConfigureAwait(false);
        FolderSnapshot after = await runtime.SnapshotService.CaptureAsync(
            runtime.Root,
            runtime.Grant,
            cancellationToken).ConfigureAwait(false);
        FixtureExecutionOutput data = new(
            execution.Status,
            execution.Receipt is null ? null : FixtureArtifacts.Project(execution.Receipt),
            FixtureArtifacts.Tree(after));
        await workspace.WriteArtifactAsync(
            ExecutionName,
            FixtureJson.Serialize(data),
            cancellationToken).ConfigureAwait(false);
        await workspace.WriteArtifactAsync(
            ExecutionFinalSnapshotName,
            FixtureArtifacts.EncodeSnapshot(after),
            cancellationToken).ConfigureAwait(false);
        StateWriteResult storedSnapshot = await runtime.StoreSnapshotAsync(
            "execution-final",
            after,
            cancellationToken).ConfigureAwait(false);
        if (!storedSnapshot.IsSuccess)
        {
            return FixturePipelineResult.Failure(storedSnapshot.FailureCode!, data);
        }

        return execution.IsVerified && after.IsComplete
            ? FixturePipelineResult.Success(execution.ReasonCode, data)
            : FixturePipelineResult.Failure(
                execution.IsVerified
                    ? after.ReasonCode ?? "fixture.execution_final_snapshot_incomplete"
                    : execution.ReasonCode,
                data);
    }

    public static async Task<FixturePipelineResult> VerifyAsync(
        FixtureWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        FixtureValueResult<FolderSnapshot> expected = await LoadSnapshotAsync(
            workspace,
            ExecutionFinalSnapshotName,
            cancellationToken).ConfigureAwait(false);
        if (!expected.IsSuccess)
        {
            return FixturePipelineResult.Failure(expected.ReasonCode);
        }

        FixtureValueResult<FixtureRuntime> opened = await FixtureRuntime.CreateAsync(
            workspace,
            clockMinute: 9,
            cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return FixturePipelineResult.Failure(opened.ReasonCode);
        }

        FixtureRuntime runtime = opened.Value!;
        ExecutionId executionId = new(workspace.Id("production:execution"));
        ExecutionJournalReadResult journal = await runtime.JournalStore.LoadJournalAsync(
            executionId,
            cancellationToken).ConfigureAwait(false);
        ExecutionReceiptReadResult receipt = await runtime.JournalStore.LoadReceiptAsync(
            executionId,
            cancellationToken).ConfigureAwait(false);
        if (!journal.IsSuccess ||
            !receipt.IsSuccess ||
            receipt.Kind != PersistedReceiptKind.Standard)
        {
            return FixturePipelineResult.Failure(
                !journal.IsSuccess ? journal.ReasonCode : receipt.ReasonCode);
        }

        StepRecoveryStatus[] statuses = Enumerable.Range(1, journal.Journal!.OperationCount)
            .Select(step => journal.Journal.AssessStep(step).Status)
            .ToArray();
        FolderSnapshot current = await runtime.SnapshotService.CaptureAsync(
            runtime.Root,
            runtime.Grant,
            cancellationToken).ConfigureAwait(false);
        FixtureVerificationOutput data = new(
            FixtureArtifacts.Project(receipt.StandardReceipt!),
            statuses,
            FixtureArtifacts.Tree(current));
        bool treeUnchanged = expected.Value!.RootIdentity == current.RootIdentity &&
            expected.Value.HashedBytes == current.HashedBytes &&
            expected.Value.Entries.SequenceEqual(current.Entries);
        return current.IsComplete &&
            expected.Value.IsComplete &&
            statuses.All(static status => status == StepRecoveryStatus.Verified) &&
            treeUnchanged
            ? FixturePipelineResult.Success("fixture.execution_verified", data)
            : FixturePipelineResult.Failure(
                treeUnchanged
                    ? "fixture.execution_verification_failed"
                    : "fixture.execution_tree_changed",
                data);
    }

    public static async Task<FixturePipelineResult> UndoAsync(
        FixtureWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        FixtureValueResult<SkillSpecContract> specification = await LoadSpecificationAsync(
            workspace,
            cancellationToken).ConfigureAwait(false);
        FixtureValueResult<FolderSnapshot> planningSnapshot = await LoadSnapshotAsync(
            workspace,
            PlanningSnapshotName,
            cancellationToken).ConfigureAwait(false);
        if (!specification.IsSuccess || !planningSnapshot.IsSuccess)
        {
            return FixturePipelineResult.Failure(
                !specification.IsSuccess
                    ? specification.ReasonCode
                    : planningSnapshot.ReasonCode);
        }

        FixtureValueResult<FixtureRuntime> opened = await FixtureRuntime.CreateAsync(
            workspace,
            clockMinute: 9,
            cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return FixturePipelineResult.Failure(opened.ReasonCode);
        }

        FixtureRuntime runtime = opened.Value!;
        FixtureValueResult<ExecutionPlan> originalPlan = await RecreatePlanAsync(
            workspace,
            runtime,
            specification.Value!,
            planningSnapshot.Value!,
            cancellationToken).ConfigureAwait(false);
        if (!originalPlan.IsSuccess)
        {
            return FixturePipelineResult.Failure(originalPlan.ReasonCode);
        }

        ExecutionId originalExecutionId = new(workspace.Id("production:execution"));
        ExecutionJournalReadResult originalJournal =
            await runtime.JournalStore.LoadJournalAsync(
                originalExecutionId,
                cancellationToken).ConfigureAwait(false);
        ExecutionReceiptReadResult originalReceipt =
            await runtime.JournalStore.LoadReceiptAsync(
                originalExecutionId,
                cancellationToken).ConfigureAwait(false);
        StateReadResult<SkillVersionStateRecord> loadedSkill =
            await runtime.StateStore.LoadSkillVersionAsync(
                originalPlan.Value!.Definition.SkillId,
                originalPlan.Value.Definition.SkillVersion,
                cancellationToken).ConfigureAwait(false);
        if (!originalJournal.IsSuccess ||
            !originalReceipt.IsSuccess ||
            originalReceipt.Kind != PersistedReceiptKind.Standard ||
            !loadedSkill.IsSuccess)
        {
            return FixturePipelineResult.Failure(
                !originalJournal.IsSuccess
                    ? originalJournal.ReasonCode
                    : !originalReceipt.IsSuccess
                        ? originalReceipt.ReasonCode
                        : loadedSkill.ReasonCode);
        }

        FolderSnapshot current = await runtime.SnapshotService.CaptureAsync(
            runtime.Root,
            runtime.Grant,
            cancellationToken).ConfigureAwait(false);
        UndoPlanningResult planning = new UndoPlanner().Plan(
            new UndoPlanningRequest(
                new PlanId(workspace.Id("undo:plan")),
                originalPlan.Value,
                originalJournal.Journal!,
                originalReceipt.StandardReceipt!,
                loadedSkill.Value!.Version,
                runtime.Grant,
                current,
                workspace.AtMinute(10),
                workspace.AtMinute(30)));
        if (!planning.IsReady)
        {
            return FixturePipelineResult.Failure(planning.ReasonCode);
        }

        RecoveryPlan recoveryPlan = planning.Plan!;
        byte[] canonicalRecovery = CanonicalRecoveryPlan.Encode(recoveryPlan.Definition);
        await workspace.WriteArtifactAsync(
            RecoveryPlanName,
            canonicalRecovery,
            cancellationToken).ConfigureAwait(false);
        StateWriteResult storedPlan = await runtime.StateStore.StoreRecoveryPlanAsync(
            recoveryPlan,
            Encoding.UTF8.GetString(canonicalRecovery),
            cancellationToken).ConfigureAwait(false);
        if (!storedPlan.IsSuccess)
        {
            return FixturePipelineResult.Failure(storedPlan.FailureCode!);
        }

        PlanApproval approval = PlanApproval.IssueUndo(
            new ApprovalId(workspace.Id("undo:approval")),
            recoveryPlan,
            workspace.AtMinute(10),
            recoveryPlan.Definition.ExpiresUtc);
        StateWriteResult storedApproval = await runtime.StateStore.StoreApprovalAsync(
            approval,
            cancellationToken).ConfigureAwait(false);
        if (!storedApproval.IsSuccess)
        {
            return FixturePipelineResult.Failure(storedApproval.FailureCode!);
        }

        FixtureValueResult<FixtureRuntime> executionRuntime =
            await FixtureRuntime.CreateAsync(
                workspace,
                clockMinute: 11,
                cancellationToken).ConfigureAwait(false);
        if (!executionRuntime.IsSuccess)
        {
            return FixturePipelineResult.Failure(executionRuntime.ReasonCode);
        }

        runtime = executionRuntime.Value!;
        var authorized = new PermissionGateway(runtime.Clock).AuthorizeUndo(
            recoveryPlan,
            loadedSkill.Value.Version,
            runtime.Grant,
            approval);
        if (!authorized.IsSuccess)
        {
            return FixturePipelineResult.Failure(authorized.Error!.Code);
        }

        FileSkillExecutor executor = new(
            runtime.Clock,
            new FixtureAuthoritySource(loadedSkill.Value.Version, runtime.Grant),
            runtime.JournalStore,
            runtime.PathSafety,
            runtime.SnapshotService,
            runtime.MutationEngine);
        UndoExecutionResult undone = await executor.ExecuteUndoAsync(
            new UndoExecutionRequest(
                new ExecutionId(workspace.Id("undo:execution")),
                new ReceiptId(workspace.Id("undo:receipt")),
                recoveryPlan,
                authorized.Value!,
                originalPlan.Value,
                originalJournal.Journal!,
                originalReceipt.StandardReceipt!,
                runtime.Root),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        FolderSnapshot restored = await runtime.SnapshotService.CaptureAsync(
            runtime.Root,
            runtime.Grant,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<FixtureTreeEntry> restoredTree = FixtureArtifacts.Tree(restored);
        bool exactTree = restoredTree.SequenceEqual(
            FixtureArtifacts.Tree(planningSnapshot.Value!));
        FixtureUndoOutput data = new(
            undone.Status,
            recoveryPlan.Fingerprint.Value,
            undone.Receipt is null ? null : FixtureArtifacts.Project(undone.Receipt),
            exactTree,
            restoredTree);
        await workspace.WriteArtifactAsync(
            UndoName,
            FixtureJson.Serialize(data),
            cancellationToken).ConfigureAwait(false);
        return undone.IsVerified && exactTree
            ? FixturePipelineResult.Success(undone.ReasonCode, data)
            : FixturePipelineResult.Failure(
                undone.IsVerified ? "fixture.undo_tree_mismatch" : undone.ReasonCode,
                data);
    }

    public static async Task<FixturePipelineResult> ExportCapsuleAsync(
        FixtureWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        FixtureValueResult<SkillSpecContract> specification = await LoadSpecificationAsync(
            workspace,
            cancellationToken).ConfigureAwait(false);
        if (!specification.IsSuccess)
        {
            return FixturePipelineResult.Failure(specification.ReasonCode);
        }

        FixtureValueResult<FixtureRuntime> opened = await FixtureRuntime.CreateAsync(
            workspace,
            clockMinute: 12,
            cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return FixturePipelineResult.Failure(opened.ReasonCode);
        }

        FixtureRuntime runtime = opened.Value!;
        StateReadResult<SkillVersionStateRecord> skill =
            await runtime.StateStore.LoadSkillVersionAsync(
                new SkillId(specification.Value!.SkillId),
                new SkillVersionNumber(specification.Value.Version),
                cancellationToken).ConfigureAwait(false);
        if (!skill.IsSuccess)
        {
            return FixturePipelineResult.Failure(skill.ReasonCode);
        }

        string canonicalSpecification = Encoding.UTF8.GetString(
            CanonicalSkillSpec.Encode(specification.Value));
        if (!string.Equals(
                canonicalSpecification,
                skill.Value!.SkillSpecJson,
                StringComparison.Ordinal) ||
            skill.Value.Version.SpecificationHash !=
                CanonicalSkillSpec.ComputeHash(specification.Value).Value ||
            specification.Value.Applicability.RootGrantId != runtime.Grant.Id.Value)
        {
            return FixturePipelineResult.Failure("fixture.skill_artifact_mismatch");
        }

        FixtureValueResult<ExportedSkillLifecycleState> exportedLifecycle =
            ExportedLifecycle(skill.Value.Version.Lifecycle);
        if (!exportedLifecycle.IsSuccess)
        {
            return FixturePipelineResult.Failure(exportedLifecycle.ReasonCode);
        }

        ExecutionReceiptReadResult receipt = await runtime.JournalStore.LoadReceiptAsync(
            new ExecutionId(workspace.Id("production:execution")),
            cancellationToken).ConfigureAwait(false);
        if (!receipt.IsSuccess && receipt.ReasonCode != "persistence.receipt_not_found")
        {
            return FixturePipelineResult.Failure(receipt.ReasonCode);
        }

        if (receipt.IsSuccess && receipt.Kind != PersistedReceiptKind.Standard)
        {
            return FixturePipelineResult.Failure("fixture.capsule_receipt_kind_invalid");
        }

        ExecutionReceipt? verifiedReceipt = receipt.IsSuccess &&
            receipt.Kind == PersistedReceiptKind.Standard
                ? receipt.StandardReceipt
                : null;
        CompanionCapsuleContract capsule = new()
        {
            SchemaVersion = ContractVersions.V1,
            CapsuleId = workspace.Id("capsule"),
            ExportedAt = workspace.AtMinute(12),
            Producer = new CapsuleProducerContract
            {
                Name = "Tooltail",
                Version = "0.1.0-alpha.1",
            },
            Companion = new CapsuleCompanionContract
            {
                CompanionId = runtime.Grant.CompanionId.Value,
                CreatedAt = workspace.Manifest.CreatedUtc,
                DisplayName = "Tooltail Fixture Companion",
                Presentation = new CapsulePresentationContract
                {
                    BodyStyle = "minimal-apprentice",
                    Accent = "#5B7CFA",
                },
            },
            Skills =
            [
                new CapsuleSkillContract
                {
                    SkillSpec = specification.Value,
                    ExportedLifecycleState = exportedLifecycle.Value,
                    SourceGrantBinding = new CapsuleSourceGrantBindingContract
                    {
                        SourceGrantId = runtime.Grant.Id.Value,
                        ImportBehavior = CapsuleImportBehavior.RequireUserRebind,
                    },
                    EvidenceSummary = new CapsuleEvidenceSummaryContract
                    {
                        VerifiedSuccessCount = verifiedReceipt is null ? 0 : 1,
                        VerifiedFailureCount = 0,
                        CorrectionCount = 0,
                        LastVerifiedAt = verifiedReceipt?.CompletedUtc,
                    },
                },
            ],
            ContentPolicy = new CapsuleContentPolicyContract
            {
                ContainsRawPaths = false,
                ContainsRawFileNames = false,
                ContainsFileContents = false,
                ContainsModelTranscripts = false,
                ContainsCredentials = false,
            },
        };
        CapsuleEncodingResult encoded = CompanionCapsuleService.Encode(capsule);
        if (!encoded.IsSuccess ||
            encoded.Bytes!.Length > FixtureWorkspace.MaximumArtifactBytes)
        {
            return FixturePipelineResult.Failure(
                encoded.IsSuccess ? "fixture.capsule_too_large" : encoded.ReasonCode);
        }

        await workspace.WriteArtifactAsync(
            CapsuleName,
            encoded.Bytes,
            cancellationToken).ConfigureAwait(false);
        FixtureCapsuleOutput data = new(
            capsule.CapsuleId,
            Convert.ToHexStringLower(SHA256.HashData(encoded.Bytes)),
            capsule.Skills.Count,
            encoded.Capsule!);
        return FixturePipelineResult.Success("fixture.capsule_exported", data);
    }

    private static SkillPlanningResult CreatePlan(
        FixtureWorkspace workspace,
        FixtureRuntime runtime,
        SkillSpecContract specification,
        FolderSnapshot snapshot) =>
        new SkillPlanner().DryRun(
            new SkillPlanningRequest(
                new PlanId(workspace.Id("production:plan")),
                specification,
                CanonicalSkillSpec.ComputeHash(specification),
                runtime.Grant,
                snapshot,
                workspace.AtMinute(5),
                workspace.AtMinute(35)));

    private static async Task<FixtureValueResult<ExecutionPlan>> RecreatePlanAsync(
        FixtureWorkspace workspace,
        FixtureRuntime runtime,
        SkillSpecContract specification,
        FolderSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        SkillPlanningResult planning = CreatePlan(workspace, runtime, specification, snapshot);
        if (planning.Status != SkillPlanningStatus.Ready)
        {
            return FixtureValueResult<ExecutionPlan>.Failure(
                planning.Diagnostics.Count > 0
                    ? planning.Diagnostics[0].Code
                    : "fixture.plan_failed");
        }

        byte[] stored = await workspace.ReadArtifactAsync(
            PlanName,
            cancellationToken).ConfigureAwait(false);
        byte[] actual = CanonicalExecutionPlan.Encode(planning.Plan!.Definition);
        return stored.AsSpan().SequenceEqual(actual)
            ? FixtureValueResult<ExecutionPlan>.Success(planning.Plan)
            : FixtureValueResult<ExecutionPlan>.Failure("fixture.plan_artifact_mismatch");
    }

    private static FixturePlanOutput Project(ExecutionPlan plan, int matchedFileCount) =>
        new(
            plan.Definition.Id.Value,
            plan.Fingerprint.Value,
            matchedFileCount,
            plan.Definition.Operations.Select(static operation => new FixturePlanOperation(
                operation.Sequence,
                operation.Primitive,
                operation.SourceRelativePath,
                operation.DestinationRelativePath,
                operation.DestinationPrecondition,
                operation.ExpectedSourceState,
                operation.ExpectedDestinationState)).ToArray());

    private static async Task<FixtureValueResult<FolderSnapshot>> LoadSnapshotAsync(
        FixtureWorkspace workspace,
        string artifactName,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes = await workspace.ReadArtifactAsync(
                artifactName,
                cancellationToken).ConfigureAwait(false);
            return FixtureArtifacts.DecodeSnapshot(bytes);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return FixtureValueResult<FolderSnapshot>.Failure(
                "fixture.snapshot_artifact_missing");
        }
    }

    private static async Task<FixtureValueResult<(
        FixtureReconciliationDocument Document,
        byte[] Bytes)>> LoadReconciliationAsync(
        FixtureWorkspace workspace,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes = await workspace.ReadArtifactAsync(
                ReconciliationName,
                cancellationToken).ConfigureAwait(false);
            FixtureValueResult<FixtureReconciliationDocument> decoded =
                FixtureArtifacts.DecodeReconciliation(bytes);
            return decoded.IsSuccess
                ? FixtureValueResult<(FixtureReconciliationDocument, byte[])>.Success(
                    (decoded.Value!, bytes))
                : FixtureValueResult<(FixtureReconciliationDocument, byte[])>.Failure(
                    decoded.ReasonCode);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return FixtureValueResult<(
                FixtureReconciliationDocument,
                byte[])>.Failure("fixture.reconciliation_artifact_missing");
        }
    }

    private static string CompilationBarrierReason(SnapshotReconciliationStatus status) =>
        status switch
        {
            SnapshotReconciliationStatus.IncompleteSnapshot => "reconcile.snapshot_incomplete",
            SnapshotReconciliationStatus.WatcherOverflow => "reconcile.watcher_overflow",
            SnapshotReconciliationStatus.WatcherFault => "reconcile.watcher_fault",
            SnapshotReconciliationStatus.WatcherNotQuiesced =>
                "reconcile.watcher_not_quiesced",
            SnapshotReconciliationStatus.RootMismatch => "reconcile.root_mismatch",
            SnapshotReconciliationStatus.Concurrent => "reconcile.concurrent_change",
            SnapshotReconciliationStatus.Ambiguous => "reconcile.ambiguous",
            SnapshotReconciliationStatus.Unsupported => "reconcile.unsupported_effect",
            _ => "fixture.reconciliation_artifact_invalid",
        };

    private static FixtureValueResult<ExportedSkillLifecycleState> ExportedLifecycle(
        SkillLifecycleState lifecycle) => lifecycle switch
        {
            SkillLifecycleState.Draft =>
                FixtureValueResult<ExportedSkillLifecycleState>.Success(
                    ExportedSkillLifecycleState.Draft),
            SkillLifecycleState.Approved =>
                FixtureValueResult<ExportedSkillLifecycleState>.Success(
                    ExportedSkillLifecycleState.Approved),
            SkillLifecycleState.Practiced =>
                FixtureValueResult<ExportedSkillLifecycleState>.Success(
                    ExportedSkillLifecycleState.Practiced),
            SkillLifecycleState.Reliable =>
                FixtureValueResult<ExportedSkillLifecycleState>.Success(
                    ExportedSkillLifecycleState.Reliable),
            SkillLifecycleState.Stale =>
                FixtureValueResult<ExportedSkillLifecycleState>.Success(
                    ExportedSkillLifecycleState.Stale),
            _ => FixtureValueResult<ExportedSkillLifecycleState>.Failure(
                "fixture.capsule_lifecycle_unsupported"),
        };

    private static async Task<FixtureValueResult<SkillSpecContract>> LoadSpecificationAsync(
        FixtureWorkspace workspace,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes = await workspace.ReadArtifactAsync(
                SkillSpecName,
                cancellationToken).ConfigureAwait(false);
            ContractParseResult<SkillSpecContract> parsed = ContractJson.ParseSkillSpec(bytes);
            if (!parsed.IsSuccess)
            {
                return FixtureValueResult<SkillSpecContract>.Failure(parsed.Error!.Code);
            }

            SkillValidationResult validation = SkillSpecValidator.Validate(parsed.Value!);
            return validation.IsValid
                ? FixtureValueResult<SkillSpecContract>.Success(parsed.Value!)
                : FixtureValueResult<SkillSpecContract>.Failure("fixture.skill_invalid");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return FixtureValueResult<SkillSpecContract>.Failure(
                "fixture.skill_artifact_missing");
        }
    }
}
