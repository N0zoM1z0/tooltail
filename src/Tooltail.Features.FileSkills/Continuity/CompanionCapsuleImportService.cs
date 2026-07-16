using System.Text;
using Tooltail.Application.Abstractions;
using Tooltail.Contracts.Capsules;
using Tooltail.Contracts.Json;
using Tooltail.Contracts.Skills;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Correction;
using Tooltail.Features.FileSkills.Skills;

namespace Tooltail.Features.FileSkills.Continuity;

public sealed record CapsuleImportPreparation(
    bool IsSuccess,
    string ReasonCode,
    CapsuleImportPreview Preview,
    CapsuleImportStateRecord? State);

public sealed record CapsuleImportResult(
    bool IsSuccess,
    string ReasonCode,
    CapsuleImportPreview Preview,
    CompanionId? ImportedCompanionId,
    int ImportedSkillVersionCount);

public sealed class CompanionCapsuleImportService
{
    private const string ImportCompilerId = "tooltail.capsule-import";
    private readonly IFileSkillStateStore stateStore;

    public CompanionCapsuleImportService(IFileSkillStateStore stateStore)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        this.stateStore = stateStore;
    }

    public static CapsuleImportPreparation Prepare(
        ReadOnlySpan<byte> utf8Json,
        CompanionId expectedEmptyCompanionId)
    {
        CapsuleImportPreview preview = CompanionCapsuleService.ParseForImport(utf8Json);
        if (!preview.IsSuccess || !preview.CanImport || preview.CreatesAuthority ||
            !preview.SkillsRequireRebind || preview.Capsule is null)
        {
            return Failure(preview.ReasonCode, preview);
        }

        if (expectedEmptyCompanionId.Value == Guid.Empty)
        {
            return Failure("capsule.import_expected_companion_invalid", preview);
        }

        CompanionCapsuleContract capsule = preview.Capsule;
        CompanionId importedCompanionId = new(capsule.Companion.CompanionId);
        CompanionStateRecord importedCompanion = new(
            importedCompanionId,
            capsule.Companion.DisplayName,
            capsule.Companion.CreatedAt,
            IdentitySchemaVersion: 1,
            Encoding.UTF8.GetString(
                ContractJson.Serialize(capsule.Companion.Presentation)));
        List<SkillVersionStateRecord> records = [];
        foreach (IGrouping<Guid, CapsuleSkillContract> history in capsule.Skills
                     .GroupBy(static skill => skill.SkillSpec.SkillId)
                     .OrderBy(static group => group.Key))
        {
            CapsuleSkillContract[] ordered = history
                .OrderBy(static skill => skill.SkillSpec.Version)
                .ToArray();
            DateTimeOffset skillCreatedUtc = ordered[0].SkillSpec.CreatedAt;
            for (int index = 0; index < ordered.Length; index++)
            {
                SkillSpecContract specification = ordered[index].SkillSpec;
                SkillSpecificationHash hash =
                    CanonicalSkillSpec.ComputeHash(specification);
                SkillVersion version = new(
                    new SkillId(specification.SkillId),
                    new SkillVersionNumber(specification.Version),
                    specification.Provenance.ParentVersion is null
                        ? null
                        : new SkillVersionNumber(
                            specification.Provenance.ParentVersion.Value),
                    hash.Value,
                    specification.Compiler.Version,
                    specification.Compatibility.MinimumExecutorVersion,
                    SkillLifecycleState.Stale,
                    specification.CreatedAt);
                string? semanticDiff = index == 0
                    ? null
                    : Encoding.UTF8.GetString(ContractJson.Serialize(
                        SkillCorrectionService.Compare(
                            ordered[index - 1].SkillSpec,
                            specification)));
                records.Add(new SkillVersionStateRecord(
                    importedCompanionId,
                    specification.Name,
                    skillCreatedUtc,
                    version,
                    MakeCurrent: index == ordered.Length - 1,
                    specification.SchemaVersion,
                    Encoding.UTF8.GetString(
                        CanonicalSkillSpec.Encode(specification)),
                    ImportCompilerId,
                    ApprovedUtc: null,
                    semanticDiff));
            }
        }

        return new CapsuleImportPreparation(
            true,
            "capsule.import_prepared",
            preview,
            new CapsuleImportStateRecord(
                expectedEmptyCompanionId,
                importedCompanion,
                records));
    }

    public async Task<CapsuleImportResult> ImportAsync(
        ReadOnlyMemory<byte> utf8Json,
        CompanionId expectedEmptyCompanionId,
        CancellationToken cancellationToken = default)
    {
        CapsuleImportPreparation prepared = Prepare(
            utf8Json.Span,
            expectedEmptyCompanionId);
        if (!prepared.IsSuccess || prepared.State is null)
        {
            return new CapsuleImportResult(
                false,
                prepared.ReasonCode,
                prepared.Preview,
                null,
                0);
        }

        StateWriteResult stored = await stateStore.ImportCapsuleAsync(
            prepared.State,
            cancellationToken).ConfigureAwait(false);
        return stored.IsSuccess
            ? new CapsuleImportResult(
                true,
                "capsule.imported_unbound",
                prepared.Preview,
                prepared.State.ImportedCompanion.Id,
                prepared.State.SkillVersions.Count)
            : new CapsuleImportResult(
                false,
                stored.FailureCode!,
                prepared.Preview,
                null,
                0);
    }

    private static CapsuleImportPreparation Failure(
        string reasonCode,
        CapsuleImportPreview preview) =>
        new(false, reasonCode, preview, null);
}
