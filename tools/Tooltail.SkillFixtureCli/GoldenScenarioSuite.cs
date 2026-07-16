using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tooltail.Contracts.Skills;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Features.FileSkills.Compilation;
using Tooltail.Features.FileSkills.Observation;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Features.FileSkills.Reconciliation;
using Tooltail.Features.FileSkills.Skills;
using Tooltail.Features.FileSkills.Snapshots;

namespace Tooltail.SkillFixtureCli;

internal sealed record GoldenSuiteOutput(
    string ContractVersion,
    string DatasetVersion,
    DateTimeOffset BaseUtc,
    bool WatcherHintPermutationInvariant,
    IReadOnlyList<GoldenScenarioOutput> Scenarios);

internal sealed record GoldenScenarioOutput(
    string Id,
    string Intent,
    string Outcome,
    GoldenPositiveEvidence? Positive,
    GoldenClarificationEvidence? Clarification,
    IReadOnlyList<GoldenRejectionFact>? Rejections);

internal sealed record GoldenPositiveEvidence(
    string ReconciliationReasonCode,
    IReadOnlyList<FixtureEffectDocument> DemonstrationEffects,
    string SpecificationSha256,
    SkillSpecContract Specification,
    JsonElement CanonicalPlan,
    FixturePlanOutput Plan,
    FixtureRehearsalOutput Rehearsal,
    FixtureReceiptProjection ExecutionReceipt,
    IReadOnlyList<StepRecoveryStatus> ReloadedStepStatuses,
    IReadOnlyList<FixtureTreeEntry> OriginalTree,
    IReadOnlyList<FixtureTreeEntry> FinalTree,
    JsonElement CanonicalRecoveryPlan,
    FixtureRecoveryReceiptProjection UndoReceipt,
    bool RestoredOriginalTree,
    IReadOnlyList<FixtureTreeEntry> RestoredTree);

internal sealed record GoldenClarificationEvidence(
    string ReconciliationReasonCode,
    IReadOnlyList<FixtureEffectDocument> DemonstrationEffects,
    IReadOnlyList<FixtureCandidateOutput> Candidates,
    IReadOnlyList<CompilerQuestion> Questions);

internal sealed record GoldenRejectionFact(
    string Evidence,
    SnapshotReconciliationStatus? ReconciliationStatus,
    string? ReconciliationReasonCode,
    string? EffectReasonCode,
    SkillCompilationStatus? CompilationStatus,
    string CompilationReasonCode,
    int CandidateCount);

internal static class GoldenScenarioSuite
{
    public const string ContractVersion = "tooltail.fixture-golden-suite/1";
    public const string DatasetVersion = "roadmap-m2/1";

    private static readonly Guid SuiteId =
        Guid.Parse("70d62a06-fad8-43c8-9b35-400ad754eade");

    private static readonly DateTimeOffset BaseUtc =
        new(2026, 7, 16, 8, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset CreationUtc =
        new(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public static async Task<FixturePipelineResult> RunAsync(
        string requestedPath,
        CancellationToken cancellationToken = default)
    {
        FixtureWorkspaceResult suiteResult = await FixtureWorkspace.CreateAsync(
            requestedPath,
            SuiteId,
            BaseUtc,
            "M2 roadmap golden suite",
            "Six deterministic headless File Apprentice acceptance scenarios.",
            cancellationToken).ConfigureAwait(false);
        if (!suiteResult.IsSuccess)
        {
            return FixturePipelineResult.Failure(suiteResult.ReasonCode);
        }

        try
        {
            PositiveScenarioDefinition[] positiveDefinitions =
            [
                MoveInvoicesDefinition(),
                RenameImagesDefinition(),
                PrefixMetadataDateDefinition(),
                CopyToReviewDefinition(),
            ];
            List<GoldenScenarioOutput> scenarios = [];
            bool watcherInvariant = false;
            foreach (PositiveScenarioDefinition definition in positiveDefinitions)
            {
                (GoldenScenarioOutput Scenario, bool WatcherInvariant) result =
                    await RunPositiveAsync(
                        suiteResult.Workspace!,
                        definition,
                        cancellationToken).ConfigureAwait(false);
                scenarios.Add(result.Scenario);
                watcherInvariant |= result.WatcherInvariant;
            }

            scenarios.Add(
                await RunClarificationAsync(
                    suiteResult.Workspace!,
                    cancellationToken).ConfigureAwait(false));
            scenarios.Add(
                await RunRejectionsAsync(
                    suiteResult.Workspace!,
                    cancellationToken).ConfigureAwait(false));
            if (!watcherInvariant)
            {
                throw Failure(
                    "01-move-invoice-pdfs",
                    "reconcile-invariance",
                    "fixture.watcher_hint_invariance_failed");
            }

            return FixturePipelineResult.Success(
                "fixture.golden_suite_passed",
                new GoldenSuiteOutput(
                    ContractVersion,
                    DatasetVersion,
                    BaseUtc,
                    watcherInvariant,
                    scenarios));
        }
        catch (GoldenScenarioException exception)
        {
            return FixturePipelineResult.Failure(
                exception.ReasonCode,
                new
                {
                    exception.ScenarioId,
                    exception.Stage,
                });
        }
    }

    private static async Task<(GoldenScenarioOutput Scenario, bool WatcherInvariant)>
        RunPositiveAsync(
            FixtureWorkspace suite,
            PositiveScenarioDefinition definition,
            CancellationToken cancellationToken)
    {
        FixtureWorkspace workspace = await CreateScenarioAsync(
            suite,
            definition.Id,
            definition.Name,
            definition.Description,
            cancellationToken).ConfigureAwait(false);
        definition.PrepareDemonstration(workspace);
        RequireSuccess(
            definition.Id,
            "snapshot-baseline",
            await FixturePipeline.SnapshotAsync(
                workspace,
                "baseline",
                cancellationToken).ConfigureAwait(false));
        definition.ApplyDemonstration(workspace);
        RequireSuccess(
            definition.Id,
            "snapshot-final",
            await FixturePipeline.SnapshotAsync(
                workspace,
                "final",
                cancellationToken).ConfigureAwait(false));

        bool watcherInvariant = definition.Id != "01-move-invoice-pdfs" ||
            await WatcherHintsAreInvariantAsync(workspace, cancellationToken).ConfigureAwait(false);
        FixturePipelineResult reconciliationResult = await FixturePipeline.ReconcileAsync(
            workspace,
            watcherOverflow: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        FixtureReconciliationOutput reconciliation = RequireData<FixtureReconciliationOutput>(
            definition.Id,
            "reconcile",
            reconciliationResult);
        FixtureCompileOutput compilation = RequireData<FixtureCompileOutput>(
            definition.Id,
            "compile",
            await FixturePipeline.CompileAsync(
                workspace,
                definition.Answers,
                cancellationToken).ConfigureAwait(false));
        RequireSuccess(
            definition.Id,
            "validate",
            await FixturePipeline.ValidateAsync(workspace, cancellationToken).ConfigureAwait(false));
        if (compilation.Specification is null || compilation.SpecificationSha256 is null)
        {
            throw Failure(definition.Id, "compile", "fixture.golden_specification_missing");
        }

        ClearOwnedRoot(workspace);
        definition.PrepareProduction(workspace);
        FixturePlanOutput plan = RequireData<FixturePlanOutput>(
            definition.Id,
            "plan",
            await FixturePipeline.PlanAsync(workspace, cancellationToken).ConfigureAwait(false));
        FolderSnapshot planningSnapshot = await ReadSnapshotAsync(
            definition.Id,
            workspace,
            "planning.snapshot.json",
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<FixtureTreeEntry> originalTree = FixtureArtifacts.Tree(planningSnapshot);
        FixtureRehearsalOutput rehearsal = RequireData<FixtureRehearsalOutput>(
            definition.Id,
            "rehearse",
            await FixturePipeline.RehearseAsync(workspace, cancellationToken).ConfigureAwait(false));
        FixtureExecutionOutput execution = RequireData<FixtureExecutionOutput>(
            definition.Id,
            "execute",
            await FixturePipeline.ExecuteAsync(workspace, cancellationToken).ConfigureAwait(false));
        FixtureVerificationOutput verification = RequireData<FixtureVerificationOutput>(
            definition.Id,
            "verify",
            await FixturePipeline.VerifyAsync(workspace, cancellationToken).ConfigureAwait(false));
        if (execution.Receipt is null ||
            !execution.FinalTree.SequenceEqual(verification.CurrentTree))
        {
            throw Failure(definition.Id, "verify", "fixture.golden_verification_mismatch");
        }

        FixtureUndoOutput undo = RequireData<FixtureUndoOutput>(
            definition.Id,
            "undo",
            await FixturePipeline.UndoAsync(workspace, cancellationToken).ConfigureAwait(false));
        if (undo.Receipt is null ||
            !undo.RestoredOriginalTree ||
            !originalTree.SequenceEqual(undo.RestoredTree))
        {
            throw Failure(definition.Id, "undo", "fixture.golden_undo_mismatch");
        }

        JsonElement canonicalPlan = await ReadJsonAsync(
            workspace,
            "plan.json",
            cancellationToken).ConfigureAwait(false);
        JsonElement canonicalRecoveryPlan = await ReadJsonAsync(
            workspace,
            "recovery-plan.json",
            cancellationToken).ConfigureAwait(false);
        GoldenPositiveEvidence evidence = new(
            reconciliationResult.ReasonCode,
            reconciliation.Effects,
            compilation.SpecificationSha256,
            compilation.Specification,
            canonicalPlan,
            plan,
            rehearsal,
            execution.Receipt,
            verification.StepStatuses,
            originalTree,
            execution.FinalTree,
            canonicalRecoveryPlan,
            undo.Receipt,
            undo.RestoredOriginalTree,
            undo.RestoredTree);
        return (
            new GoldenScenarioOutput(
                definition.Id,
                definition.Intent,
                "completed_and_undone",
                evidence,
                Clarification: null,
                Rejections: null),
            watcherInvariant);
    }

    private static async Task<GoldenScenarioOutput> RunClarificationAsync(
        FixtureWorkspace suite,
        CancellationToken cancellationToken)
    {
        const string id = "05-clarify-date-source";
        FixtureWorkspace workspace = await CreateScenarioAsync(
            suite,
            id,
            "Clarify date prefix source",
            "Ask whether a date-looking prefix is fixed text or file metadata.",
            cancellationToken).ConfigureAwait(false);
        WriteFile(workspace, "Inbox\\report-one.txt", "report one", BaseUtc.AddDays(-2));
        WriteFile(workspace, "Inbox\\report-two.txt", "report two", BaseUtc.AddDays(-1));
        RequireSuccess(
            id,
            "snapshot-baseline",
            await FixturePipeline.SnapshotAsync(
                workspace,
                "baseline",
                cancellationToken).ConfigureAwait(false));
        MoveFile(
            workspace,
            "Inbox\\report-one.txt",
            "Inbox\\2026-07-report-one.txt");
        MoveFile(
            workspace,
            "Inbox\\report-two.txt",
            "Inbox\\2026-07-report-two.txt");
        RequireSuccess(
            id,
            "snapshot-final",
            await FixturePipeline.SnapshotAsync(
                workspace,
                "final",
                cancellationToken).ConfigureAwait(false));
        FixturePipelineResult reconciliationResult = await FixturePipeline.ReconcileAsync(
            workspace,
            watcherOverflow: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        FixtureReconciliationOutput reconciliation = RequireData<FixtureReconciliationOutput>(
            id,
            "reconcile",
            reconciliationResult);
        FixturePipelineResult compileResult = await FixturePipeline.CompileAsync(
            workspace,
            ScopeAnswers(),
            cancellationToken).ConfigureAwait(false);
        if (compileResult.ExitCode != 3 ||
            compileResult.Data is not FixtureCompileOutput compilation ||
            compilation.Status != SkillCompilationStatus.NeedsClarification ||
            compilation.Questions.Count != 1 ||
            compilation.Questions[0].Code != "transform.filename")
        {
            throw Failure(id, "compile", compileResult.ReasonCode);
        }

        return new GoldenScenarioOutput(
            id,
            "Ask whether a date-like constant is fixed text or last-write metadata.",
            "needs_clarification",
            Positive: null,
            new GoldenClarificationEvidence(
                reconciliationResult.ReasonCode,
                reconciliation.Effects,
                compilation.Candidates,
                compilation.Questions),
            Rejections: null);
    }

    private static async Task<GoldenScenarioOutput> RunRejectionsAsync(
        FixtureWorkspace suite,
        CancellationToken cancellationToken)
    {
        List<GoldenRejectionFact> facts =
        [
            await RunSnapshotRejectionAsync(
                suite,
                "06a-deletion",
                RejectionSnapshotKind.Deletion,
                cancellationToken).ConfigureAwait(false),
            await RunSnapshotRejectionAsync(
                suite,
                "06b-content-modification",
                RejectionSnapshotKind.ContentModification,
                cancellationToken).ConfigureAwait(false),
            await RunSnapshotRejectionAsync(
                suite,
                "06c-reparse-point",
                RejectionSnapshotKind.ReparsePoint,
                cancellationToken).ConfigureAwait(false),
            await RunCrossVolumeRejectionAsync(
                suite,
                cancellationToken).ConfigureAwait(false),
            await RunOverflowRejectionAsync(
                suite,
                cancellationToken).ConfigureAwait(false),
        ];
        return new GoldenScenarioOutput(
            "06-reject-unsupported-lessons",
            "Reject deletion, content modification, reparse, cross-volume, and overflow evidence.",
            "rejected",
            Positive: null,
            Clarification: null,
            facts);
    }

    private static async Task<GoldenRejectionFact> RunSnapshotRejectionAsync(
        FixtureWorkspace suite,
        string id,
        RejectionSnapshotKind kind,
        CancellationToken cancellationToken)
    {
        FixtureWorkspace workspace = await CreateScenarioAsync(
            suite,
            id,
            "Reject unsupported lesson",
            "A deterministic negative evidence fixture.",
            cancellationToken).ConfigureAwait(false);
        FixtureRuntime runtime = await OpenRuntimeAsync(
            id,
            workspace,
            cancellationToken).ConfigureAwait(false);
        (FolderSnapshot Baseline, FolderSnapshot Final) snapshots =
            CreateRejectionSnapshots(runtime, workspace, kind);
        await PersistSyntheticSnapshotsAsync(
            id,
            workspace,
            runtime,
            snapshots.Baseline,
            snapshots.Final,
            cancellationToken).ConfigureAwait(false);
        FixturePipelineResult reconciliationResult = await FixturePipeline.ReconcileAsync(
            workspace,
            watcherOverflow: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (reconciliationResult.IsSuccess ||
            reconciliationResult.Data is not FixtureReconciliationOutput reconciliation ||
            reconciliation.Effects.Count != 1)
        {
            throw Failure(id, "reconcile", reconciliationResult.ReasonCode);
        }

        FixturePipelineResult compilation = await FixturePipeline.CompileAsync(
            workspace,
            ScopeAnswers(),
            cancellationToken).ConfigureAwait(false);
        if (compilation.IsSuccess)
        {
            throw Failure(id, "compile", "fixture.unsupported_evidence_compiled");
        }

        return new GoldenRejectionFact(
            kind switch
            {
                RejectionSnapshotKind.Deletion => "deletion",
                RejectionSnapshotKind.ContentModification => "content_modification",
                RejectionSnapshotKind.ReparsePoint => "reparse_point",
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            },
            reconciliation.Status,
            reconciliationResult.ReasonCode,
            reconciliation.Effects[0].ReasonCode,
            CompilationStatus: null,
            compilation.ReasonCode,
            CandidateCount: 0);
    }

    private static async Task<GoldenRejectionFact> RunCrossVolumeRejectionAsync(
        FixtureWorkspace suite,
        CancellationToken cancellationToken)
    {
        const string id = "06d-cross-volume";
        FixtureWorkspace workspace = await CreateScenarioAsync(
            suite,
            id,
            "Reject cross-volume movement",
            "Compiler evidence must prove one same local volume.",
            cancellationToken).ConfigureAwait(false);
        FixtureRuntime runtime = await OpenRuntimeAsync(
            id,
            workspace,
            cancellationToken).ConfigureAwait(false);
        TeachingFileExample[] examples =
        [
            CompilerExample(
                workspace,
                runtime,
                1,
                "Inbox\\invoice-one.pdf",
                "Invoices\\invoice-one.pdf",
                "invoice one",
                "volume-a",
                "volume-a"),
            CompilerExample(
                workspace,
                runtime,
                2,
                "Inbox\\invoice-two.pdf",
                "Invoices\\invoice-two.pdf",
                "invoice two",
                "volume-a",
                "volume-b"),
        ];
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
                ScopeAnswers()));
        if (compilation.Status != SkillCompilationStatus.InvalidRequest ||
            compilation.ReasonCode != "compiler.evidence_scope_mismatch" ||
            compilation.Candidates.Count != 0)
        {
            throw Failure(id, "compile", compilation.ReasonCode);
        }

        return new GoldenRejectionFact(
            "cross_volume_movement",
            ReconciliationStatus: null,
            ReconciliationReasonCode: null,
            EffectReasonCode: null,
            compilation.Status,
            compilation.ReasonCode,
            compilation.Candidates.Count);
    }

    private static async Task<GoldenRejectionFact> RunOverflowRejectionAsync(
        FixtureWorkspace suite,
        CancellationToken cancellationToken)
    {
        const string id = "06e-watcher-overflow";
        FixtureWorkspace workspace = await CreateScenarioAsync(
            suite,
            id,
            "Reject watcher overflow",
            "An overflowed teaching observation cannot compile from snapshots alone.",
            cancellationToken).ConfigureAwait(false);
        FixtureRuntime runtime = await OpenRuntimeAsync(
            id,
            workspace,
            cancellationToken).ConfigureAwait(false);
        FolderSnapshotEntry first = SyntheticFile(
            "Inbox\\invoice-one.pdf",
            "invoice one",
            "file-one",
            runtime.Root.VolumeIdentity,
            BaseUtc.AddDays(-2));
        FolderSnapshotEntry second = SyntheticFile(
            "Inbox\\invoice-two.pdf",
            "invoice two",
            "file-two",
            runtime.Root.VolumeIdentity,
            BaseUtc.AddDays(-1));
        FolderSnapshot baseline = Snapshot(
            runtime,
            workspace.AtMinute(1),
            first,
            second);
        FolderSnapshot final = Snapshot(
            runtime,
            workspace.AtMinute(2),
            Relocate(first, "Invoices\\invoice-one.pdf"),
            Relocate(second, "Invoices\\invoice-two.pdf"));
        await PersistSyntheticSnapshotsAsync(
            id,
            workspace,
            runtime,
            baseline,
            final,
            cancellationToken).ConfigureAwait(false);
        FixturePipelineResult reconciliationResult = await FixturePipeline.ReconcileAsync(
            workspace,
            watcherOverflow: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (reconciliationResult.IsSuccess ||
            reconciliationResult.Data is not FixtureReconciliationOutput reconciliation ||
            reconciliation.Status != SnapshotReconciliationStatus.WatcherOverflow)
        {
            throw Failure(id, "reconcile", reconciliationResult.ReasonCode);
        }

        FixturePipelineResult compilation = await FixturePipeline.CompileAsync(
            workspace,
            ScopeAnswers(),
            cancellationToken).ConfigureAwait(false);
        if (compilation.IsSuccess || compilation.ReasonCode != "reconcile.watcher_overflow")
        {
            throw Failure(id, "compile", compilation.ReasonCode);
        }

        return new GoldenRejectionFact(
            "watcher_overflow",
            reconciliation.Status,
            reconciliationResult.ReasonCode,
            EffectReasonCode: null,
            CompilationStatus: null,
            compilation.ReasonCode,
            CandidateCount: 0);
    }

    private static (FolderSnapshot Baseline, FolderSnapshot Final) CreateRejectionSnapshots(
        FixtureRuntime runtime,
        FixtureWorkspace workspace,
        RejectionSnapshotKind kind)
    {
        return kind switch
        {
            RejectionSnapshotKind.Deletion =>
                (
                    Snapshot(
                        runtime,
                        workspace.AtMinute(1),
                        SyntheticFile(
                            "Inbox\\obsolete.txt",
                            "obsolete",
                            "stable-file",
                            runtime.Root.VolumeIdentity,
                            BaseUtc.AddDays(-1))),
                    Snapshot(runtime, workspace.AtMinute(2))),
            RejectionSnapshotKind.ContentModification => CreateModificationSnapshots(
                runtime,
                workspace),
            RejectionSnapshotKind.ReparsePoint => CreateReparseSnapshots(
                runtime,
                workspace),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static (FolderSnapshot Baseline, FolderSnapshot Final) CreateModificationSnapshots(
        FixtureRuntime runtime,
        FixtureWorkspace workspace)
    {
        FolderSnapshotEntry before = SyntheticFile(
            "Inbox\\report.txt",
            "before",
            "stable-file",
            runtime.Root.VolumeIdentity,
            BaseUtc.AddDays(-2));
        FolderSnapshotEntry after = SyntheticFile(
            "Inbox\\report.txt",
            "after",
            "stable-file",
            runtime.Root.VolumeIdentity,
            BaseUtc.AddDays(-1));
        return (
            Snapshot(runtime, workspace.AtMinute(1), before),
            Snapshot(runtime, workspace.AtMinute(2), after));
    }

    private static (FolderSnapshot Baseline, FolderSnapshot Final) CreateReparseSnapshots(
        FixtureRuntime runtime,
        FixtureWorkspace workspace)
    {
        FolderSnapshotEntry link = new(
            "LinkedFolder",
            SnapshotEntryKind.Directory,
            length: null,
            CreationUtc,
            BaseUtc.AddDays(-1),
            FileAttributes.Directory | FileAttributes.ReparsePoint,
            isReparsePoint: true,
            runtime.Root.VolumeIdentity,
            "reparse-entry",
            SnapshotContentHashStatus.NotApplicable,
            contentHash: null);
        return (
            Snapshot(runtime, workspace.AtMinute(1), link),
            Snapshot(runtime, workspace.AtMinute(2), link));
    }

    private static async Task PersistSyntheticSnapshotsAsync(
        string scenarioId,
        FixtureWorkspace workspace,
        FixtureRuntime runtime,
        FolderSnapshot baseline,
        FolderSnapshot final,
        CancellationToken cancellationToken)
    {
        await workspace.WriteArtifactAsync(
            "baseline.snapshot.json",
            FixtureArtifacts.EncodeSnapshot(baseline),
            cancellationToken).ConfigureAwait(false);
        await workspace.WriteArtifactAsync(
            "final.snapshot.json",
            FixtureArtifacts.EncodeSnapshot(final),
            cancellationToken).ConfigureAwait(false);
        var storedBaseline = await runtime.StoreSnapshotAsync(
            "baseline",
            baseline,
            cancellationToken).ConfigureAwait(false);
        var storedFinal = await runtime.StoreSnapshotAsync(
            "final",
            final,
            cancellationToken).ConfigureAwait(false);
        if (!storedBaseline.IsSuccess || !storedFinal.IsSuccess)
        {
            throw Failure(
                scenarioId,
                "persist-snapshots",
                storedBaseline.FailureCode ?? storedFinal.FailureCode ??
                    "fixture.snapshot_persistence_failed");
        }
    }

    private static FolderSnapshot Snapshot(
        FixtureRuntime runtime,
        DateTimeOffset startedUtc,
        params FolderSnapshotEntry[] entries)
    {
        long hashedBytes = entries
            .Where(static entry =>
                entry.ContentHashStatus == SnapshotContentHashStatus.Computed)
            .Sum(static entry => entry.Length!.Value);
        var restored = FolderSnapshot.Rehydrate(
            runtime.Root.Identity,
            startedUtc,
            startedUtc.AddSeconds(1),
            FolderSnapshotStatus.Complete,
            reasonCode: null,
            hashedBytes,
            entries.OrderBy(static entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase));
        return restored.IsSuccess
            ? restored.Value!
            : throw new InvalidOperationException(restored.Error!.Code);
    }

    private static FolderSnapshotEntry SyntheticFile(
        string relativePath,
        string content,
        string identity,
        string volumeIdentity,
        DateTimeOffset lastWriteUtc)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        return new FolderSnapshotEntry(
            relativePath,
            SnapshotEntryKind.File,
            bytes.Length,
            CreationUtc,
            lastWriteUtc,
            FileAttributes.Archive,
            isReparsePoint: false,
            volumeIdentity,
            identity,
            SnapshotContentHashStatus.Computed,
            new ContentHash(Convert.ToHexStringLower(SHA256.HashData(bytes))));
    }

    private static FolderSnapshotEntry Relocate(
        FolderSnapshotEntry source,
        string destination) =>
        new(
            destination,
            source.Kind,
            source.Length,
            source.CreationUtc,
            source.LastWriteUtc,
            source.Attributes,
            source.IsReparsePoint,
            source.VolumeIdentity,
            source.EntryIdentity,
            source.ContentHashStatus,
            source.ContentHash);

    private static TeachingFileExample CompilerExample(
        FixtureWorkspace workspace,
        FixtureRuntime runtime,
        int sequence,
        string sourcePath,
        string destinationPath,
        string content,
        string sourceVolume,
        string destinationVolume)
    {
        FolderSnapshotEntry source = SyntheticFile(
            sourcePath,
            content,
            $"source-{sequence}",
            sourceVolume,
            BaseUtc.AddDays(-sequence));
        FolderSnapshotEntry destination = new(
            destinationPath,
            source.Kind,
            source.Length,
            source.CreationUtc,
            source.LastWriteUtc,
            source.Attributes,
            source.IsReparsePoint,
            destinationVolume,
            $"destination-{sequence}",
            source.ContentHashStatus,
            source.ContentHash);
        ReconciledFileEffect effect = new(
            ReconciledEffectKind.Moved,
            sourcePath,
            destinationPath,
            source,
            destination,
            "reconcile.synthetic_cross_volume");
        return new TeachingFileExample(
            new ExampleId(workspace.Id($"cross-volume-example:{sequence}")),
            new TeachingEpisodeId(workspace.Id("episode")),
            runtime.Grant.Id,
            runtime.Root.Identity,
            effect);
    }

    private static async Task<bool> WatcherHintsAreInvariantAsync(
        FixtureWorkspace workspace,
        CancellationToken cancellationToken)
    {
        FolderSnapshot baseline = await ReadSnapshotAsync(
            "01-move-invoice-pdfs",
            workspace,
            "baseline.snapshot.json",
            cancellationToken).ConfigureAwait(false);
        FolderSnapshot final = await ReadSnapshotAsync(
            "01-move-invoice-pdfs",
            workspace,
            "final.snapshot.json",
            cancellationToken).ConfigureAwait(false);
        WatcherHint first = new(
            WatcherHintKind.Renamed,
            "Invoices\\invoice-alpha.pdf",
            "Inbox\\invoice-alpha.pdf");
        WatcherHint second = new(
            WatcherHintKind.Renamed,
            "Invoices\\invoice-beta.pdf",
            "Inbox\\invoice-beta.pdf");
        SnapshotReconciliation withoutHints = SnapshotReconciler.Reconcile(
            baseline,
            final,
            WatcherHintBatch.Empty);
        SnapshotReconciliation ordered = SnapshotReconciler.Reconcile(
            baseline,
            final,
            new WatcherHintBatch(
                [first, second],
                overflowed: false,
                quiesced: true));
        SnapshotReconciliation reorderedAndDuplicated = SnapshotReconciler.Reconcile(
            baseline,
            final,
            new WatcherHintBatch(
                [second, first, second, first],
                overflowed: false,
                quiesced: true));
        byte[] expected = FixtureArtifacts.EncodeReconciliation(withoutHints);
        return expected.AsSpan().SequenceEqual(FixtureArtifacts.EncodeReconciliation(ordered)) &&
            expected.AsSpan().SequenceEqual(
                FixtureArtifacts.EncodeReconciliation(reorderedAndDuplicated));
    }

    private static PositiveScenarioDefinition MoveInvoicesDefinition() =>
        new(
            "01-move-invoice-pdfs",
            "Move PDFs whose names contain invoice into Invoices.",
            "File invoice PDFs",
            "Move invoice PDFs into Invoices without overwriting anything.",
            static workspace =>
            {
                WriteFile(
                    workspace,
                    "Inbox\\invoice-alpha.pdf",
                    "invoice alpha",
                    BaseUtc.AddDays(-4));
                WriteFile(
                    workspace,
                    "Inbox\\invoice-beta.pdf",
                    "invoice beta",
                    BaseUtc.AddDays(-3));
                WriteFile(
                    workspace,
                    "Inbox\\notes.txt",
                    "notes",
                    BaseUtc.AddDays(-2));
            },
            static workspace =>
            {
                MoveFile(
                    workspace,
                    "Inbox\\invoice-alpha.pdf",
                    "Invoices\\invoice-alpha.pdf");
                MoveFile(
                    workspace,
                    "Inbox\\invoice-beta.pdf",
                    "Invoices\\invoice-beta.pdf");
            },
            static workspace =>
            {
                EnsureDirectory(workspace, "Invoices");
                WriteFile(
                    workspace,
                    "Inbox\\invoice-march.pdf",
                    "invoice march",
                    BaseUtc.AddDays(-8));
                WriteFile(
                    workspace,
                    "Inbox\\invoice-april.pdf",
                    "invoice april",
                    BaseUtc.AddDays(-7));
                WriteFile(
                    workspace,
                    "Inbox\\notes.txt",
                    "notes",
                    BaseUtc.AddDays(-2));
            },
            ScopeAnswers());

    private static PositiveScenarioDefinition RenameImagesDefinition() =>
        new(
            "02-rename-image-stems",
            "Rename image stems from spaces to lower-case hyphens.",
            "Normalize photo names",
            "Lower-case and hyphenate image filename stems without changing extensions.",
            static workspace =>
            {
                WriteFile(
                    workspace,
                    "Photos\\Summer Trip.JPG",
                    "summer trip",
                    BaseUtc.AddDays(-6));
                WriteFile(
                    workspace,
                    "Photos\\Family Portrait.JPG",
                    "family portrait",
                    BaseUtc.AddDays(-5));
                WriteFile(
                    workspace,
                    "Photos\\notes.txt",
                    "notes",
                    BaseUtc.AddDays(-2));
            },
            static workspace =>
            {
                MoveFile(
                    workspace,
                    "Photos\\Summer Trip.JPG",
                    "Photos\\summer-trip.JPG");
                MoveFile(
                    workspace,
                    "Photos\\Family Portrait.JPG",
                    "Photos\\family-portrait.JPG");
            },
            static workspace =>
            {
                WriteFile(
                    workspace,
                    "Photos\\Beach Day.JPG",
                    "beach day",
                    BaseUtc.AddDays(-9));
                WriteFile(
                    workspace,
                    "Photos\\Night Sky.JPG",
                    "night sky",
                    BaseUtc.AddDays(-8));
                WriteFile(
                    workspace,
                    "Photos\\notes.txt",
                    "notes",
                    BaseUtc.AddDays(-2));
            },
            OriginOnlyAnswers());

    private static PositiveScenarioDefinition PrefixMetadataDateDefinition() =>
        new(
            "03-prefix-last-write-month",
            "Prefix selected files with their last-write year and month.",
            "Prefix dated reports",
            "Prefix matching report files with typed last-write year and month metadata.",
            static workspace =>
            {
                WriteFile(
                    workspace,
                    "Inbox\\report-one.txt",
                    "report one",
                    new DateTimeOffset(2025, 11, 3, 8, 0, 0, TimeSpan.Zero));
                WriteFile(
                    workspace,
                    "Inbox\\report-two.txt",
                    "report two",
                    new DateTimeOffset(2026, 1, 4, 8, 0, 0, TimeSpan.Zero));
                WriteFile(
                    workspace,
                    "Inbox\\notes.txt",
                    "notes",
                    BaseUtc.AddDays(-2));
            },
            static workspace =>
            {
                MoveFile(
                    workspace,
                    "Inbox\\report-one.txt",
                    "Inbox\\2025-11-report-one.txt");
                MoveFile(
                    workspace,
                    "Inbox\\report-two.txt",
                    "Inbox\\2026-01-report-two.txt");
            },
            static workspace =>
            {
                WriteFile(
                    workspace,
                    "Inbox\\report-three.txt",
                    "report three",
                    new DateTimeOffset(2024, 2, 7, 8, 0, 0, TimeSpan.Zero));
                WriteFile(
                    workspace,
                    "Inbox\\report-four.txt",
                    "report four",
                    new DateTimeOffset(2026, 6, 8, 8, 0, 0, TimeSpan.Zero));
                WriteFile(
                    workspace,
                    "Inbox\\notes.txt",
                    "notes",
                    BaseUtc.AddDays(-2));
            },
            ScopeAnswers());

    private static PositiveScenarioDefinition CopyToReviewDefinition() =>
        new(
            "04-copy-to-review",
            "Copy matched files into Review while preserving originals.",
            "Copy review documents",
            "Copy matching review documents into Review without changing their sources.",
            static workspace =>
            {
                WriteFile(
                    workspace,
                    "Inbox\\review-alpha.docx",
                    "review alpha",
                    BaseUtc.AddDays(-6));
                WriteFile(
                    workspace,
                    "Inbox\\review-beta.docx",
                    "review beta",
                    BaseUtc.AddDays(-5));
                WriteFile(
                    workspace,
                    "Inbox\\notes.txt",
                    "notes",
                    BaseUtc.AddDays(-2));
            },
            static workspace =>
            {
                CopyFile(
                    workspace,
                    "Inbox\\review-alpha.docx",
                    "Review\\review-alpha.docx");
                CopyFile(
                    workspace,
                    "Inbox\\review-beta.docx",
                    "Review\\review-beta.docx");
            },
            static workspace =>
            {
                EnsureDirectory(workspace, "Review");
                WriteFile(
                    workspace,
                    "Inbox\\review-gamma.docx",
                    "review gamma",
                    BaseUtc.AddDays(-9));
                WriteFile(
                    workspace,
                    "Inbox\\review-delta.docx",
                    "review delta",
                    BaseUtc.AddDays(-8));
                WriteFile(
                    workspace,
                    "Inbox\\notes.txt",
                    "notes",
                    BaseUtc.AddDays(-2));
            },
            ScopeAnswers());

    private static async Task<FixtureWorkspace> CreateScenarioAsync(
        FixtureWorkspace suite,
        string id,
        string name,
        string description,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(suite.RootPath, id);
        FixtureWorkspaceResult created = await FixtureWorkspace.CreateAsync(
            path,
            FixtureIdentity.Derive(SuiteId, id),
            BaseUtc,
            name,
            description,
            cancellationToken).ConfigureAwait(false);
        if (!created.IsSuccess)
        {
            throw Failure(id, "initialize", created.ReasonCode);
        }

        await OpenRuntimeAsync(id, created.Workspace!, cancellationToken).ConfigureAwait(false);
        return created.Workspace!;
    }

    private static async Task<FixtureRuntime> OpenRuntimeAsync(
        string id,
        FixtureWorkspace workspace,
        CancellationToken cancellationToken)
    {
        FixtureValueResult<FixtureRuntime> opened = await FixtureRuntime.CreateAsync(
            workspace,
            clockMinute: 0,
            cancellationToken).ConfigureAwait(false);
        return opened.IsSuccess
            ? opened.Value!
            : throw Failure(id, "initialize-runtime", opened.ReasonCode);
    }

    private static async Task<FolderSnapshot> ReadSnapshotAsync(
        string scenarioId,
        FixtureWorkspace workspace,
        string artifactName,
        CancellationToken cancellationToken)
    {
        byte[] bytes = await workspace.ReadArtifactAsync(
            artifactName,
            cancellationToken).ConfigureAwait(false);
        FixtureValueResult<FolderSnapshot> decoded = FixtureArtifacts.DecodeSnapshot(bytes);
        return decoded.IsSuccess
            ? decoded.Value!
            : throw Failure(scenarioId, "read-snapshot", decoded.ReasonCode);
    }

    private static async Task<JsonElement> ReadJsonAsync(
        FixtureWorkspace workspace,
        string artifactName,
        CancellationToken cancellationToken)
    {
        byte[] bytes = await workspace.ReadArtifactAsync(
            artifactName,
            cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }

    private static void RequireSuccess(
        string scenarioId,
        string stage,
        FixturePipelineResult result)
    {
        if (!result.IsSuccess)
        {
            throw Failure(scenarioId, stage, result.ReasonCode);
        }
    }

    private static T RequireData<T>(
        string scenarioId,
        string stage,
        FixturePipelineResult result)
        where T : class
    {
        RequireSuccess(scenarioId, stage, result);
        return result.Data as T ??
            throw Failure(scenarioId, stage, "fixture.golden_output_missing");
    }

    private static void WriteFile(
        FixtureWorkspace workspace,
        string relativePath,
        string content,
        DateTimeOffset lastWriteUtc)
    {
        string path = PhysicalPath(workspace, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Encoding.UTF8);
        File.SetAttributes(path, FileAttributes.Normal);
        File.SetCreationTimeUtc(path, lastWriteUtc.UtcDateTime);
        File.SetLastWriteTimeUtc(path, lastWriteUtc.UtcDateTime);
    }

    private static void MoveFile(
        FixtureWorkspace workspace,
        string sourceRelativePath,
        string destinationRelativePath)
    {
        string source = PhysicalPath(workspace, sourceRelativePath);
        string destination = PhysicalPath(workspace, destinationRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        FileAttributes sourceAttributes = File.GetAttributes(source);
        File.Move(source, destination);
        File.SetAttributes(destination, sourceAttributes);
    }

    private static void EnsureDirectory(
        FixtureWorkspace workspace,
        string relativePath) =>
        Directory.CreateDirectory(PhysicalPath(workspace, relativePath));

    private static void CopyFile(
        FixtureWorkspace workspace,
        string sourceRelativePath,
        string destinationRelativePath)
    {
        string source = PhysicalPath(workspace, sourceRelativePath);
        string destination = PhysicalPath(workspace, destinationRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination);
        File.SetAttributes(destination, File.GetAttributes(source));
        File.SetCreationTimeUtc(destination, File.GetCreationTimeUtc(source));
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
    }

    private static string PhysicalPath(
        FixtureWorkspace workspace,
        string relativePath)
    {
        PathSafetyResult<WindowsRelativePath> parsed =
            WindowsPathPolicy.ParseRelative(relativePath);
        if (!parsed.IsSuccess)
        {
            throw new ArgumentException(parsed.Error!.Code, nameof(relativePath));
        }

        return parsed.Value!.Value
            .Split('\\')
            .Aggregate(workspace.RootPath, Path.Combine);
    }

    private static void ClearOwnedRoot(FixtureWorkspace workspace)
    {
        DirectoryInfo root = new(workspace.RootPath);
        Stack<DirectoryInfo> pending = new();
        pending.Push(root);
        int inspectedEntries = 0;
        while (pending.Count > 0)
        {
            DirectoryInfo directory = pending.Pop();
            directory.Refresh();
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
                directory.LinkTarget is not null)
            {
                throw Failure(
                    workspace.Manifest.SkillName,
                    "reset-production-fixture",
                    "fixture.reparse_point_rejected");
            }

            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
            {
                inspectedEntries++;
                if (inspectedEntries > FolderSnapshotLimits.Default.MaximumEntries)
                {
                    throw Failure(
                        workspace.Manifest.SkillName,
                        "reset-production-fixture",
                        "fixture.entry_limit_exceeded");
                }

                entry.Refresh();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    entry.LinkTarget is not null)
                {
                    throw Failure(
                        workspace.Manifest.SkillName,
                        "reset-production-fixture",
                        "fixture.reparse_point_rejected");
                }

                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                }
            }
        }

        foreach (FileSystemInfo entry in root.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo directory)
            {
                directory.Delete(recursive: true);
            }
            else
            {
                entry.Delete();
            }
        }
    }

    private static SkillUserAnswerContract[] ScopeAnswers() =>
    [
        new SkillUserAnswerContract
        {
            QuestionCode = "match.origin_scope",
            SelectedValue = "same_directory",
        },
        new SkillUserAnswerContract
        {
            QuestionCode = "match.filename_scope",
            SelectedValue = "contains_token",
        },
    ];

    private static SkillUserAnswerContract[] OriginOnlyAnswers() =>
    [
        new SkillUserAnswerContract
        {
            QuestionCode = "match.origin_scope",
            SelectedValue = "same_directory",
        },
    ];

    private static GoldenScenarioException Failure(
        string scenarioId,
        string stage,
        string reasonCode) =>
        new(scenarioId, stage, reasonCode);

    private sealed record PositiveScenarioDefinition(
        string Id,
        string Intent,
        string Name,
        string Description,
        Action<FixtureWorkspace> PrepareDemonstration,
        Action<FixtureWorkspace> ApplyDemonstration,
        Action<FixtureWorkspace> PrepareProduction,
        IReadOnlyList<SkillUserAnswerContract> Answers);

    private sealed class GoldenScenarioException(
        string scenarioId,
        string stage,
        string reasonCode) : Exception(reasonCode)
    {
        public string ScenarioId { get; } = scenarioId;

        public string Stage { get; } = stage;

        public string ReasonCode { get; } = reasonCode;
    }

    private enum RejectionSnapshotKind
    {
        Deletion,
        ContentModification,
        ReparsePoint,
    }
}
