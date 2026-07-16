using System.Text;
using Tooltail.Application.Abstractions;
using Tooltail.Contracts.Json;
using Tooltail.Contracts.Skills;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Skills;

namespace Tooltail.Features.FileSkills.Continuity;

public sealed record CapsulePersistedRebindResult(
    bool IsSuccess,
    string ReasonCode,
    SkillSpecContract? Parent,
    SkillSpecContract? Rebound,
    SkillVersionStateRecord? PersistedVersion,
    SkillId? ReboundSkillId,
    int RemainingStaleSkillCount);

public sealed class CompanionCapsuleRebindPersistenceService
{
    private readonly IFileSkillStateStore stateStore;
    private readonly IClock clock;

    public CompanionCapsuleRebindPersistenceService(
        IFileSkillStateStore stateStore,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(clock);
        this.stateStore = stateStore;
        this.clock = clock;
    }

    public async Task<CapsulePersistedRebindResult> RebindNextAsync(
        CompanionId companionId,
        LocalFolderGrant grant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (companionId.Value == Guid.Empty || grant.CompanionId != companionId ||
            !grant.Allows(GrantCapability.Enumerate, clock.UtcNow))
        {
            return Failure("capsule.rebind_grant_invalid");
        }

        StateReadResult<FileSkillWorkspaceStateRecord> workspace =
            await stateStore.LoadWorkspaceStateAsync(
                companionId,
                cancellationToken).ConfigureAwait(false);
        if (!workspace.IsSuccess)
        {
            return Failure(workspace.ReasonCode);
        }

        LocalFolderGrant? exactGrant = workspace.Value!.Grants
            .Select(static stored => stored.Grant)
            .SingleOrDefault(candidate => candidate.Id == grant.Id);
        if (exactGrant is null || !SameGrant(exactGrant, grant))
        {
            return Failure("capsule.rebind_grant_changed");
        }

        SkillVersionStateRecord? imported = workspace.Value.CurrentSkills
            .Where(static skill => skill.Version.Lifecycle == SkillLifecycleState.Stale)
            .OrderBy(static skill => skill.Version.SkillId.Value)
            .FirstOrDefault();
        if (imported is null)
        {
            return Failure("capsule.rebind_no_stale_skill");
        }

        ContractParseResult<SkillSpecContract> parsed = ContractJson.ParseSkillSpec(
            Encoding.UTF8.GetBytes(imported.SkillSpecJson));
        if (!parsed.IsSuccess || parsed.Value is null)
        {
            return Failure(parsed.Error?.Code ?? "capsule.rebind_skill_invalid");
        }

        CapsuleSkillRebindResult rebound = CompanionCapsuleRebindService.Rebind(
            parsed.Value,
            grant.Id,
            clock.UtcNow);
        if (!rebound.IsSuccess)
        {
            return Failure(rebound.ReasonCode);
        }

        SkillSpecContract specification = rebound.Rebound!;
        SkillVersion version = new(
            imported.Version.SkillId,
            new SkillVersionNumber(specification.Version),
            imported.Version.Number,
            rebound.SpecificationHash!.Value,
            specification.Compiler.Version,
            specification.Compatibility.MinimumExecutorVersion,
            SkillLifecycleState.Draft,
            specification.CreatedAt);
        SkillVersionStateRecord record = new(
            companionId,
            imported.DisplayName,
            imported.SkillCreatedUtc,
            version,
            MakeCurrent: true,
            specification.SchemaVersion,
            Encoding.UTF8.GetString(CanonicalSkillSpec.Encode(specification)),
            "tooltail.capsule-rebind",
            ApprovedUtc: null,
            rebound.SemanticDiffJson);
        StateWriteResult stored = await stateStore.StoreSkillVersionAsync(
            record,
            cancellationToken).ConfigureAwait(false);
        if (!stored.IsSuccess)
        {
            return Failure(stored.FailureCode!);
        }

        int remaining = workspace.Value.CurrentSkills.Count(skill =>
            skill.Version.Lifecycle == SkillLifecycleState.Stale &&
            skill.Version.SkillId != imported.Version.SkillId);
        return new CapsulePersistedRebindResult(
            true,
            "capsule.rebind_draft_persisted",
            parsed.Value,
            specification,
            record,
            imported.Version.SkillId,
            remaining);
    }

    private static CapsulePersistedRebindResult Failure(string reasonCode) =>
        new(false, reasonCode, null, null, null, null, 0);

    private static bool SameGrant(
        LocalFolderGrant persisted,
        LocalFolderGrant supplied) =>
        persisted.Id == supplied.Id &&
        persisted.CompanionId == supplied.CompanionId &&
        persisted.RootIdentity == supplied.RootIdentity &&
        persisted.IssuedAt == supplied.IssuedAt &&
        persisted.ExpiresAt == supplied.ExpiresAt &&
        persisted.State == supplied.State &&
        persisted.RevokedAt == supplied.RevokedAt &&
        string.Equals(
            persisted.RevocationReason,
            supplied.RevocationReason,
            StringComparison.Ordinal) &&
        persisted.Capabilities.Order().SequenceEqual(
            supplied.Capabilities.Order());
}
