using System.IO;
using Tooltail.Application.Abstractions;
using Tooltail.Contracts.Capsules;
using Tooltail.Contracts.Json;
using Tooltail.Contracts.Skills;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Continuity;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Infrastructure.Sqlite;

namespace Tooltail.Desktop.Presentation;

public sealed record CapsuleExportWorkflowResult(
    bool IsSuccess,
    string ReasonCode,
    string? CanonicalPath,
    int ByteCount,
    int SkillVersionCount,
    CapsuleImportPreview? Preview);

public sealed class CapsuleExportWorkflowService
{
    private readonly TooltailSqliteDatabase database;
    private readonly IFileSkillStateStore stateStore;
    private readonly WindowsPathSafetyService pathSafety;
    private readonly IClock clock;
    private readonly IIdGenerator idGenerator;

    public CapsuleExportWorkflowService(
        TooltailSqliteDatabase database,
        IFileSkillStateStore stateStore,
        WindowsPathSafetyService pathSafety,
        IClock clock,
        IIdGenerator idGenerator)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(pathSafety);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(idGenerator);
        this.database = database;
        this.stateStore = stateStore;
        this.pathSafety = pathSafety;
        this.clock = clock;
        this.idGenerator = idGenerator;
    }

    public async Task<CapsuleExportWorkflowResult> ExportAsync(
        CompanionId companionId,
        CancellationToken cancellationToken = default)
    {
        StateReadResult<FileSkillWorkspaceStateRecord> workspace =
            await stateStore.LoadWorkspaceStateAsync(
                companionId,
                cancellationToken).ConfigureAwait(false);
        if (!workspace.IsSuccess || workspace.Value!.CurrentSkills.Count == 0)
        {
            return Failure(
                workspace.IsSuccess ? "capsule.no_skills" : workspace.ReasonCode);
        }

        List<SkillVersionStateRecord> versions = [];
        foreach (SkillVersionStateRecord currentSkill in workspace.Value.CurrentSkills)
        {
            StateReadResult<IReadOnlyList<SkillVersionStateRecord>> history =
                await stateStore.LoadSkillVersionsAsync(
                    currentSkill.Version.SkillId,
                    cancellationToken).ConfigureAwait(false);
            if (!history.IsSuccess)
            {
                return Failure(history.ReasonCode);
            }

            versions.AddRange(history.Value!);
        }

        CapsuleSkillContract[] skills = new CapsuleSkillContract[versions.Count];
        for (int index = 0; index < versions.Count; index++)
        {
            SkillVersionStateRecord version = versions[index];
            ContractParseResult<SkillSpecContract> parsed =
                ContractJson.ParseSkillSpec(
                    System.Text.Encoding.UTF8.GetBytes(version.SkillSpecJson));
            if (!parsed.IsSuccess)
            {
                return Failure(parsed.Error!.Code);
            }

            ExecutionSummaryStateRecord[] verified = workspace.Value.Executions
                .Where(execution =>
                    execution.SkillId == version.Version.SkillId &&
                    execution.SkillVersion == version.Version.Number &&
                    execution.Status == PersistedExecutionStatus.Verified &&
                    execution.HasReceipt)
                .ToArray();
            skills[index] = new CapsuleSkillContract
            {
                SkillSpec = parsed.Value!,
                ExportedLifecycleState = ExportLifecycle(version.Version.Lifecycle),
                SourceGrantBinding = new CapsuleSourceGrantBindingContract
                {
                    SourceGrantId = parsed.Value!.Applicability.RootGrantId,
                    ImportBehavior = CapsuleImportBehavior.RequireUserRebind,
                },
                EvidenceSummary = new CapsuleEvidenceSummaryContract
                {
                    VerifiedSuccessCount = verified.Length,
                    VerifiedFailureCount = workspace.Value.Executions.Count(execution =>
                        execution.SkillId == version.Version.SkillId &&
                        execution.SkillVersion == version.Version.Number &&
                        execution.Status is PersistedExecutionStatus.Failed or
                            PersistedExecutionStatus.RecoveryRequired),
                    CorrectionCount = version.Version.Parent is null ? 0 : 1,
                    LastVerifiedAt = verified
                        .Select(static execution => execution.CompletedUtc)
                        .Where(static completed => completed is not null)
                        .Max(),
                },
            };
        }

        Guid capsuleId = idGenerator.NewId();
        DateTimeOffset exportedUtc = clock.UtcNow;
        CompanionCapsuleContract capsule = new()
        {
            SchemaVersion = ContractVersions.V1,
            CapsuleId = capsuleId,
            ExportedAt = exportedUtc,
            Producer = new CapsuleProducerContract
            {
                Name = "Tooltail",
                Version = "0.1.0",
            },
            Companion = new CapsuleCompanionContract
            {
                CompanionId = workspace.Value.Companion.Id.Value,
                CreatedAt = workspace.Value.Companion.CreatedUtc,
                DisplayName = workspace.Value.Companion.DisplayName,
                Presentation = new CapsulePresentationContract
                {
                    BodyStyle = "pip-default",
                    Accent = "#4A90E2",
                },
            },
            Skills = skills,
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
        if (!encoded.IsSuccess || encoded.Bytes is null)
        {
            return Failure(encoded.ReasonCode);
        }

        CapsuleImportPreview preview = CompanionCapsuleService.ParseForImport(encoded.Bytes);
        if (!preview.IsSuccess ||
            preview.CreatesAuthority ||
            preview.CanImport ||
            !preview.SkillsRequireRebind)
        {
            return Failure(preview.ReasonCode);
        }

        PathSafetyResult<CanonicalLocalRoot> exportRoot = EnsureOwnedExportRoot();
        if (!exportRoot.IsSuccess)
        {
            return Failure(exportRoot.Error!.Code);
        }

        string fileName = $"companion-{capsuleId:N}.tooltail-capsule.json";
        PathSafetyResult<BoundLocalPath> destination = pathSafety.Bind(
            exportRoot.Value!,
            fileName,
            PathEntryExpectation.MustNotExist);
        if (!destination.IsSuccess)
        {
            return Failure(destination.Error!.Code);
        }

        PathSafetyResult<BoundLocalPath> current = pathSafety.Revalidate(
            destination.Value!);
        if (!current.IsSuccess)
        {
            return Failure(current.Error!.Code);
        }

        try
        {
            await using FileStream stream = new(
                current.Value!.FullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(encoded.Bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new CapsuleExportWorkflowResult(
                true,
                "capsule.exported",
                current.Value.FullPath,
                encoded.Bytes.Length,
                skills.Length,
                preview);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure("capsule.export_cancelled");
        }
        catch (UnauthorizedAccessException)
        {
            return Failure("capsule.export_access_denied");
        }
        catch (IOException)
        {
            return Failure("capsule.export_io_failure");
        }
    }

    private PathSafetyResult<CanonicalLocalRoot> EnsureOwnedExportRoot()
    {
        string? stateDirectory = Path.GetDirectoryName(database.DatabasePath);
        string? applicationRootPath = stateDirectory is null
            ? null
            : Path.GetDirectoryName(stateDirectory);
        PathSafetyResult<CanonicalLocalRoot> applicationRoot =
            pathSafety.CaptureRoot(applicationRootPath);
        if (!applicationRoot.IsSuccess)
        {
            return applicationRoot;
        }

        PathSafetyResult<BoundLocalPath> existing = pathSafety.Bind(
            applicationRoot.Value!,
            "Exports",
            PathEntryExpectation.MayExist);
        if (!existing.IsSuccess)
        {
            return PathSafetyResult.Failure<CanonicalLocalRoot>(
                existing.Error!.Code,
                existing.Error.Message);
        }

        if (!existing.Value!.Components[^1].Existed)
        {
            PathSafetyResult<BoundLocalPath> current = pathSafety.Revalidate(existing.Value);
            if (!current.IsSuccess)
            {
                return PathSafetyResult.Failure<CanonicalLocalRoot>(
                    current.Error!.Code,
                    current.Error.Message);
            }

            Directory.CreateDirectory(current.Value!.FullPath);
        }

        return pathSafety.CaptureSubroot(applicationRoot.Value!, "Exports");
    }

    private static ExportedSkillLifecycleState ExportLifecycle(
        SkillLifecycleState lifecycle) => lifecycle switch
        {
            SkillLifecycleState.Draft => ExportedSkillLifecycleState.Draft,
            SkillLifecycleState.Approved => ExportedSkillLifecycleState.Approved,
            SkillLifecycleState.Practiced => ExportedSkillLifecycleState.Practiced,
            SkillLifecycleState.Reliable or SkillLifecycleState.Delegated =>
                ExportedSkillLifecycleState.Reliable,
            SkillLifecycleState.Stale => ExportedSkillLifecycleState.Stale,
            _ => throw new ArgumentOutOfRangeException(nameof(lifecycle)),
        };

    private static CapsuleExportWorkflowResult Failure(string reasonCode) =>
        new(false, reasonCode, null, 0, 0, null);
}
