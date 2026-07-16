using Tooltail.Application.Abstractions;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;

namespace Tooltail.Features.FileSkills.Execution;

public sealed class FileSkillStateExecutionAuthoritySource : IExecutionAuthoritySource
{
    private readonly IFileSkillStateStore stateStore;

    public FileSkillStateExecutionAuthoritySource(IFileSkillStateStore stateStore)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        this.stateStore = stateStore;
    }

    public async ValueTask<ExecutionAuthorityState?> ReadCurrentAsync(
        SkillId skillId,
        SkillVersionNumber skillVersion,
        GrantId grantId,
        CancellationToken cancellationToken = default)
    {
        StateReadResult<SkillVersionStateRecord> storedSkill =
            await stateStore.LoadSkillVersionAsync(
                skillId,
                skillVersion,
                cancellationToken).ConfigureAwait(false);
        if (!storedSkill.IsSuccess)
        {
            return null;
        }

        StateReadResult<FileSkillWorkspaceStateRecord> workspace =
            await stateStore.LoadWorkspaceStateAsync(
                storedSkill.Value!.CompanionId,
                cancellationToken).ConfigureAwait(false);
        if (!workspace.IsSuccess)
        {
            return null;
        }

        LocalFolderGrant? grant = workspace.Value!.Grants
            .Select(static item => item.Grant)
            .FirstOrDefault(item => item.Id == grantId);
        return grant is null
            ? null
            : new ExecutionAuthorityState(storedSkill.Value.Version, grant);
    }
}
