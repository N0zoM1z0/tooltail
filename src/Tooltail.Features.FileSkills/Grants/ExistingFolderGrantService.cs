using Tooltail.Application.Abstractions;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Features.FileSkills.Paths;

namespace Tooltail.Features.FileSkills.Grants;

public sealed record ExistingFolderGrantPreview(
    Guid RequestId,
    CompanionId CompanionId,
    CanonicalLocalRoot Root,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc);

public sealed record ExistingFolderGrantPreviewResult(
    bool IsSuccess,
    string ReasonCode,
    ExistingFolderGrantPreview? Preview);

public sealed record ExistingFolderGrantIssueResult(
    bool IsSuccess,
    string ReasonCode,
    LocalFolderGrant? Grant,
    CanonicalLocalRoot? Root,
    byte[]? ProtectedCanonicalRoot);

public sealed class ExistingFolderGrantService
{
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan GrantLifetime = TimeSpan.FromDays(7);
    private readonly WindowsPathSafetyService pathSafety;
    private readonly IFileSkillStateStore stateStore;
    private readonly ILocalFolderRootProtector rootProtector;
    private readonly IClock clock;
    private readonly IIdGenerator idGenerator;

    public ExistingFolderGrantService(
        WindowsPathSafetyService pathSafety,
        IFileSkillStateStore stateStore,
        ILocalFolderRootProtector rootProtector,
        IClock clock,
        IIdGenerator idGenerator)
    {
        ArgumentNullException.ThrowIfNull(pathSafety);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(rootProtector);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(idGenerator);
        this.pathSafety = pathSafety;
        this.stateStore = stateStore;
        this.rootProtector = rootProtector;
        this.clock = clock;
        this.idGenerator = idGenerator;
    }

    public ExistingFolderGrantPreviewResult Preview(
        CompanionId companionId,
        string selectedPath)
    {
        DateTimeOffset now = clock.UtcNow;
        if (companionId.Value == Guid.Empty || now.Offset != TimeSpan.Zero ||
            string.IsNullOrWhiteSpace(selectedPath))
        {
            return PreviewFailure("folder_grant.preview_invalid");
        }

        PathSafetyResult<CanonicalLocalRoot> root = pathSafety.CaptureRoot(selectedPath);
        return root.IsSuccess
            ? new ExistingFolderGrantPreviewResult(
                true,
                "folder_grant.preview_ready",
                new ExistingFolderGrantPreview(
                    idGenerator.NewId(),
                    companionId,
                    root.Value!,
                    now,
                    now.Add(PreviewLifetime)))
            : PreviewFailure(root.Error!.Code);
    }

    public async Task<ExistingFolderGrantIssueResult> ConfirmAsync(
        ExistingFolderGrantPreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        DateTimeOffset now = clock.UtcNow;
        if (preview.RequestId == Guid.Empty ||
            preview.CompanionId.Value == Guid.Empty ||
            preview.IssuedUtc.Offset != TimeSpan.Zero ||
            preview.ExpiresUtc.Offset != TimeSpan.Zero ||
            preview.ExpiresUtc <= preview.IssuedUtc ||
            preview.ExpiresUtc - preview.IssuedUtc > PreviewLifetime ||
            now.Offset != TimeSpan.Zero || now < preview.IssuedUtc ||
            now > preview.ExpiresUtc)
        {
            return IssueFailure("folder_grant.preview_expired_or_invalid");
        }

        PathSafetyResult<CanonicalLocalRoot> current = pathSafety.CaptureRoot(
            preview.Root.CanonicalPath);
        if (!current.IsSuccess || current.Value!.Identity != preview.Root.Identity ||
            !string.Equals(
                current.Value.CanonicalPath,
                preview.Root.CanonicalPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return IssueFailure(current.IsSuccess
                ? "folder_grant.root_identity_changed"
                : current.Error!.Code);
        }

        StateReadResult<FileSkillWorkspaceStateRecord> workspace =
            await stateStore.LoadWorkspaceStateAsync(
                preview.CompanionId,
                cancellationToken).ConfigureAwait(false);
        if (!workspace.IsSuccess)
        {
            return IssueFailure(workspace.ReasonCode);
        }

        if (workspace.Value!.Companion.Id != preview.CompanionId ||
            workspace.Value.Grants.Any(grant =>
                grant.Grant.State == ResourceGrantState.Active &&
                (grant.Grant.ExpiresAt is null || grant.Grant.ExpiresAt > now)))
        {
            return IssueFailure("folder_grant.active_grant_exists");
        }

        RootProtectionResult protectedRoot = rootProtector.Protect(
            current.Value.CanonicalPath);
        if (!protectedRoot.IsSuccess ||
            protectedRoot.ProtectedCanonicalRoot is not { Length: > 0 })
        {
            return IssueFailure(protectedRoot.ReasonCode);
        }

        cancellationToken.ThrowIfCancellationRequested();
        LocalFolderGrant grant = LocalFolderGrant.Issue(
            new GrantId(idGenerator.NewId()),
            preview.CompanionId,
            current.Value.Identity,
            LocalFolderGrantPolicy.FileApprenticeCapabilities,
            now,
            now.Add(GrantLifetime));
        byte[] protectedBytes = protectedRoot.ProtectedCanonicalRoot.ToArray();
        StateWriteResult stored = await stateStore.TryIssueExclusiveLocalFolderGrantAsync(
            new LocalFolderGrantStateRecord(grant, protectedBytes),
            now,
            cancellationToken).ConfigureAwait(false);
        return stored.IsSuccess
            ? new ExistingFolderGrantIssueResult(
                true,
                "folder_grant.issued",
                grant,
                current.Value,
                protectedBytes)
            : IssueFailure(string.Equals(
                stored.FailureCode,
                "persistence.active_folder_grant_exists",
                StringComparison.Ordinal)
                ? "folder_grant.active_grant_exists"
                : stored.FailureCode!);
    }

    private static ExistingFolderGrantPreviewResult PreviewFailure(string reasonCode) =>
        new(false, reasonCode, null);

    private static ExistingFolderGrantIssueResult IssueFailure(string reasonCode) =>
        new(false, reasonCode, null, null, null);
}
