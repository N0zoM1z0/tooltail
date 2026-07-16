using Tooltail.Application.Abstractions;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Features.FileSkills.Execution;

namespace Tooltail.Infrastructure.Sqlite.Tests;

public sealed class ExecutionAuthoritySourceTests
{
    [Fact]
    public async Task ReadsCurrentPersistedSkillAndGrantAndReflectsRevocation()
    {
        using SqlitePersistenceTestContext context =
            await SqlitePersistenceTestContext.CreateAsync();
        await context.SeedAuthorityAndSkillAsync();
        FileSkillStateExecutionAuthoritySource source = new(context.StateStore);

        ExecutionAuthorityState? current = await source.ReadCurrentAsync(
            SqlitePersistenceTestContext.SkillId,
            context.SkillVersion.Number,
            SqlitePersistenceTestContext.GrantId);
        ExecutionAuthorityState? wrongGrant = await source.ReadCurrentAsync(
            SqlitePersistenceTestContext.SkillId,
            context.SkillVersion.Number,
            new GrantId(Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee")));
        LocalFolderGrant revoked = context.Grant.Revoke(
            SqlitePersistenceTestContext.Now.AddMinutes(2),
            "test.revoked").Value!;
        StateWriteResult stored = await context.StateStore.StoreLocalFolderGrantAsync(
            new LocalFolderGrantStateRecord(revoked, [1, 2, 3, 4]));
        ExecutionAuthorityState? afterRevocation = await source.ReadCurrentAsync(
            SqlitePersistenceTestContext.SkillId,
            context.SkillVersion.Number,
            SqlitePersistenceTestContext.GrantId);

        Assert.NotNull(current);
        Assert.Equal(context.SkillVersion, current.SkillVersion);
        Assert.Equal(ResourceGrantState.Active, current.Grant.State);
        Assert.Null(wrongGrant);
        Assert.True(stored.IsSuccess, stored.FailureCode);
        Assert.NotNull(afterRevocation);
        Assert.Equal(ResourceGrantState.Revoked, afterRevocation.Grant.State);
    }
}
