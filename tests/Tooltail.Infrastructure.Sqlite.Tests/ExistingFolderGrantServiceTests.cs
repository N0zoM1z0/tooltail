using Tooltail.Application.Abstractions;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Features.FileSkills.Grants;
using Tooltail.Features.FileSkills.Paths;

namespace Tooltail.Infrastructure.Sqlite.Tests;

public sealed class ExistingFolderGrantServiceTests
{
    private const string Root = "C:\\Users\\Tester\\Existing";

    [Fact]
    public async Task PreviewCreatesNoAuthorityAndConfirmationPersistsExactProtectedGrant()
    {
        using SqlitePersistenceTestContext context =
            await SqlitePersistenceTestContext.CreateAsync();
        await StoreCompanionAsync(context);
        SyntheticPathProbe probe = CreateProbe();
        FakeRootProtector protector = new();
        ExistingFolderGrantService service = new(
            new WindowsPathSafetyService(probe),
            context.StateStore,
            protector,
            new MutableClock(SqlitePersistenceTestContext.Now),
            new SequenceIds(
                Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
                Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")));

        ExistingFolderGrantPreviewResult preview = service.Preview(
            SqlitePersistenceTestContext.CompanionId,
            Root + "\\");

        Assert.True(preview.IsSuccess, preview.ReasonCode);
        Assert.Equal(Root, preview.Preview!.Root.CanonicalPath);
        StateReadResult<FileSkillWorkspaceStateRecord> before =
            await context.StateStore.LoadWorkspaceStateAsync(
                SqlitePersistenceTestContext.CompanionId);
        Assert.Empty(before.Value!.Grants);

        ExistingFolderGrantIssueResult issued = await service.ConfirmAsync(
            preview.Preview);

        Assert.True(issued.IsSuccess, issued.ReasonCode);
        Assert.Equal(
            Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
            issued.Grant!.Id.Value);
        Assert.Equal(
            LocalFolderGrantPolicy.FileApprenticeCapabilities.OrderBy(static value => value),
            issued.Grant.Capabilities.OrderBy(static value => value));
        Assert.Equal(protector.ProtectedBytes, issued.ProtectedCanonicalRoot);
        StateReadResult<FileSkillWorkspaceStateRecord> after =
            await context.StateStore.LoadWorkspaceStateAsync(
                SqlitePersistenceTestContext.CompanionId);
        LocalFolderGrantStateRecord stored = Assert.Single(after.Value!.Grants);
        Assert.Equal(issued.Grant.Id, stored.Grant.Id);
        Assert.Equal(issued.Grant.CompanionId, stored.Grant.CompanionId);
        Assert.Equal(issued.Grant.RootIdentity, stored.Grant.RootIdentity);
        Assert.Equal(issued.Grant.IssuedAt, stored.Grant.IssuedAt);
        Assert.Equal(issued.Grant.ExpiresAt, stored.Grant.ExpiresAt);
        Assert.Equal(issued.Grant.State, stored.Grant.State);
        Assert.Equal(
            issued.Grant.Capabilities.OrderBy(static value => value),
            stored.Grant.Capabilities.OrderBy(static value => value));
        Assert.Equal(protector.ProtectedBytes, stored.ProtectedCanonicalRoot);
        Assert.Equal(Root, protector.LastProtectedValue);
    }

    [Fact]
    public async Task ChangedIdentityExpiryAndExistingActiveAuthorityFailClosed()
    {
        using SqlitePersistenceTestContext context =
            await SqlitePersistenceTestContext.CreateAsync();
        await StoreCompanionAsync(context);
        SyntheticPathProbe probe = CreateProbe();
        MutableClock clock = new(SqlitePersistenceTestContext.Now);
        ExistingFolderGrantService service = new(
            new WindowsPathSafetyService(probe),
            context.StateStore,
            new FakeRootProtector(),
            clock,
            new SequenceIds(
                Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
                Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
                Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"),
                Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd"),
                Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee")));
        ExistingFolderGrantPreview changed = service.Preview(
            SqlitePersistenceTestContext.CompanionId,
            Root).Preview!;
        probe.SetFound(Root, "root-v2");

        ExistingFolderGrantIssueResult changedResult =
            await service.ConfirmAsync(changed);
        Assert.False(changedResult.IsSuccess);
        Assert.Equal("folder_grant.root_identity_changed", changedResult.ReasonCode);

        probe.SetFound(Root, "root-v1");
        ExistingFolderGrantPreview expired = service.Preview(
            SqlitePersistenceTestContext.CompanionId,
            Root).Preview!;
        clock.UtcNow = expired.ExpiresUtc.AddTicks(1);
        ExistingFolderGrantIssueResult expiredResult =
            await service.ConfirmAsync(expired);
        Assert.False(expiredResult.IsSuccess);
        Assert.Equal("folder_grant.preview_expired_or_invalid", expiredResult.ReasonCode);

        clock.UtcNow = SqlitePersistenceTestContext.Now;
        ExistingFolderGrantPreview valid = service.Preview(
            SqlitePersistenceTestContext.CompanionId,
            Root).Preview!;
        ExistingFolderGrantIssueResult first = await service.ConfirmAsync(valid);
        Assert.True(first.IsSuccess, first.ReasonCode);
        ExistingFolderGrantPreview second = service.Preview(
            SqlitePersistenceTestContext.CompanionId,
            Root).Preview!;
        ExistingFolderGrantIssueResult duplicate = await service.ConfirmAsync(second);
        Assert.False(duplicate.IsSuccess);
        Assert.Equal("folder_grant.active_grant_exists", duplicate.ReasonCode);
    }

    private static async Task StoreCompanionAsync(SqlitePersistenceTestContext context)
    {
        StateWriteResult stored = await context.StateStore.StoreCompanionAsync(
            new CompanionStateRecord(
                SqlitePersistenceTestContext.CompanionId,
                "Picker test companion",
                SqlitePersistenceTestContext.Now,
                1,
                "{}"));
        Assert.True(stored.IsSuccess, stored.FailureCode);
    }

    private static SyntheticPathProbe CreateProbe()
    {
        SyntheticPathProbe probe = new();
        probe.SetFound("C:\\", "drive-v1");
        probe.SetFound("C:\\Users", "users-v1");
        probe.SetFound("C:\\Users\\Tester", "tester-v1");
        probe.SetFound(Root, "root-v1");
        return probe;
    }

    private sealed class SyntheticPathProbe : IFileSystemPathProbe
    {
        private readonly Dictionary<string, FileSystemPathProbeResult> results =
            new(StringComparer.OrdinalIgnoreCase);

        public FileSystemPathProbeResult Probe(string absolutePath) =>
            results.TryGetValue(Trim(absolutePath), out FileSystemPathProbeResult? value)
                ? value
                : FileSystemPathProbeResult.Failed(
                    FileSystemPathProbeStatus.NotFound,
                    "synthetic.not_found");

        public void SetFound(string path, string identity)
        {
            string canonical = Trim(path);
            results[canonical] = FileSystemPathProbeResult.Found(
                canonical,
                FileSystemEntryKind.Directory,
                "volume-a",
                identity,
                isReparsePoint: false,
                isLocalFixedDrive: true);
        }

        private static string Trim(string path) =>
            path.Length > 3 ? path.TrimEnd('\\') : path;
    }

    private sealed class FakeRootProtector : ILocalFolderRootProtector
    {
        public byte[] ProtectedBytes { get; } = [7, 4, 2, 9];

        public string? LastProtectedValue { get; private set; }

        public RootProtectionResult Protect(string canonicalRoot)
        {
            LastProtectedValue = canonicalRoot;
            return new RootProtectionResult(
                true,
                "root_protection.protected",
                ProtectedBytes.ToArray());
        }

        public RootUnprotectionResult Unprotect(ReadOnlySpan<byte> protectedCanonicalRoot) =>
            new(false, "test.not_used", null);
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class SequenceIds(params Guid[] values) : IIdGenerator
    {
        private readonly Queue<Guid> values = new(values);

        public Guid NewId() => values.Dequeue();
    }
}
