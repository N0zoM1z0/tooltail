using System.IO;
using System.Text;
using Tooltail.Application.Abstractions;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Features.FileSkills.Grants;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Infrastructure.Sqlite;

namespace Tooltail.Desktop.Presentation;

public sealed record SafeLabGrantResult(
    bool IsSuccess,
    string ReasonCode,
    LocalFolderGrant? Grant,
    string? CanonicalLabPath,
    CanonicalLocalRoot? Root,
    byte[]? ProtectedCanonicalRoot = null,
    bool IsTooltailOwnedLab = true);

public sealed class SafeLabGrantService
{
    private static readonly (string Name, byte[] Content)[] SeedFiles =
    [
        ("invoice-alpha.pdf", Encoding.ASCII.GetBytes("%PDF-1.4\nTooltail safe lab alpha\n")),
        ("invoice-beta.pdf", Encoding.ASCII.GetBytes("%PDF-1.4\nTooltail safe lab beta\n")),
        ("invoice-edge.pdf", Encoding.ASCII.GetBytes("%PDF-1.4\nTooltail safe lab edge\n")),
    ];

    private readonly TooltailSqliteDatabase database;
    private readonly WindowsPathSafetyService pathSafety;
    private readonly IFileSkillStateStore stateStore;
    private readonly IClock clock;
    private readonly IIdGenerator idGenerator;
    private readonly ILocalFolderRootProtector rootProtector;

    public SafeLabGrantService(
        TooltailSqliteDatabase database,
        WindowsPathSafetyService pathSafety,
        IFileSkillStateStore stateStore,
        IClock clock,
        IIdGenerator idGenerator,
        ILocalFolderRootProtector rootProtector)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(pathSafety);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(idGenerator);
        ArgumentNullException.ThrowIfNull(rootProtector);
        this.database = database;
        this.pathSafety = pathSafety;
        this.stateStore = stateStore;
        this.clock = clock;
        this.idGenerator = idGenerator;
        this.rootProtector = rootProtector;
    }

    public async Task<SafeLabGrantResult> CreateAsync(
        CompanionId companionId,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = clock.UtcNow;
        if (companionId.Value == Guid.Empty || now.Offset != TimeSpan.Zero)
        {
            return Failure("safe_lab.request_invalid");
        }

        string? stateDirectory = Path.GetDirectoryName(database.DatabasePath);
        string? applicationRootPath = stateDirectory is null
            ? null
            : Path.GetDirectoryName(stateDirectory);
        PathSafetyResult<CanonicalLocalRoot> applicationRoot =
            pathSafety.CaptureRoot(applicationRootPath);
        if (!applicationRoot.IsSuccess)
        {
            return Failure(applicationRoot.Error!.Code);
        }

        PathSafetyResult<CanonicalLocalRoot> labsRoot = EnsureOwnedDirectory(
            applicationRoot.Value!,
            "Labs");
        if (!labsRoot.IsSuccess)
        {
            return Failure(labsRoot.Error!.Code);
        }

        GrantId grantId = new(idGenerator.NewId());
        string labDirectory = grantId.Value.ToString("D");
        PathSafetyResult<CanonicalLocalRoot> labRoot = EnsureNewOwnedDirectory(
            labsRoot.Value!,
            labDirectory);
        if (!labRoot.IsSuccess)
        {
            return Failure(labRoot.Error!.Code);
        }

        foreach ((string name, byte[] content) in SeedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PathSafetyResult<BoundLocalPath> destination = pathSafety.Bind(
                labRoot.Value!,
                name,
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

            await using FileStream stream = new(
                current.Value!.FullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        LocalFolderGrant grant = LocalFolderGrant.Issue(
            grantId,
            companionId,
            labRoot.Value!.Identity,
            LocalFolderGrantPolicy.FileApprenticeCapabilities,
            now,
            now.AddDays(7));
        StateWriteResult stored = await stateStore.StoreLocalFolderGrantAsync(
            new LocalFolderGrantStateRecord(grant, ProtectedCanonicalRoot: null),
            cancellationToken).ConfigureAwait(false);
        return stored.IsSuccess
            ? new SafeLabGrantResult(
                true,
                "safe_lab.grant_issued",
                grant,
                labRoot.Value.CanonicalPath,
                labRoot.Value)
            : Failure(stored.FailureCode!);
    }

    public SafeLabGrantResult TryRestore(LocalFolderGrantStateRecord storedGrant)
    {
        ArgumentNullException.ThrowIfNull(storedGrant);
        LocalFolderGrant grant = storedGrant.Grant;
        ArgumentNullException.ThrowIfNull(grant);
        if (!grant.Allows(GrantCapability.Enumerate, clock.UtcNow))
        {
            return Failure("safe_lab.grant_inactive");
        }

        bool isOwnedSafeLab = storedGrant.ProtectedCanonicalRoot is null;
        string? labPath;
        if (isOwnedSafeLab)
        {
            string? stateDirectory = Path.GetDirectoryName(database.DatabasePath);
            string? applicationRoot = stateDirectory is null
                ? null
                : Path.GetDirectoryName(stateDirectory);
            labPath = applicationRoot is null
                ? null
                : Path.Combine(applicationRoot, "Labs", grant.Id.Value.ToString("D"));
        }
        else
        {
            RootUnprotectionResult unprotected = rootProtector.Unprotect(
                storedGrant.ProtectedCanonicalRoot);
            if (!unprotected.IsSuccess)
            {
                return Failure(unprotected.ReasonCode);
            }

            labPath = unprotected.CanonicalRoot;
        }

        PathSafetyResult<CanonicalLocalRoot> restored = pathSafety.CaptureRoot(labPath);
        return restored.IsSuccess && restored.Value!.Identity == grant.RootIdentity
            ? new SafeLabGrantResult(
                true,
                "safe_lab.restored",
                grant,
                restored.Value.CanonicalPath,
                restored.Value,
                storedGrant.ProtectedCanonicalRoot?.ToArray(),
                isOwnedSafeLab)
            : Failure(restored.IsSuccess
                ? "safe_lab.identity_changed"
                : restored.Error!.Code);
    }

    public async Task<SafeLabGrantResult> RevokeAsync(
        SafeLabGrantResult lab,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lab);
        if (!lab.IsSuccess || lab.Grant is null || lab.Root is null ||
            lab.CanonicalLabPath is null)
        {
            return Failure("safe_lab.grant_missing");
        }

        StateReadResult<FileSkillWorkspaceStateRecord> workspace =
            await stateStore.LoadWorkspaceStateAsync(
                lab.Grant.CompanionId,
                cancellationToken).ConfigureAwait(false);
        if (!workspace.IsSuccess)
        {
            return Failure(workspace.ReasonCode);
        }

        LocalFolderGrant? current = workspace.Value!.Grants
            .Select(static stored => stored.Grant)
            .SingleOrDefault(grant => grant.Id == lab.Grant.Id);
        if (current is null || current.RootIdentity != lab.Root.Identity)
        {
            return Failure("safe_lab.grant_identity_changed");
        }

        Tooltail.Domain.Common.DomainResult<LocalFolderGrant> revoked = current.Revoke(
            clock.UtcNow,
            "user_revoked");
        if (!revoked.IsSuccess)
        {
            return Failure(revoked.Error!.Code);
        }

        StateWriteResult stored = await stateStore.StoreLocalFolderGrantAsync(
            new LocalFolderGrantStateRecord(
                revoked.Value!,
                lab.ProtectedCanonicalRoot?.ToArray()),
            cancellationToken).ConfigureAwait(false);
        return stored.IsSuccess
            ? new SafeLabGrantResult(
                true,
                "safe_lab.grant_revoked",
                revoked.Value,
                lab.CanonicalLabPath,
                lab.Root,
                lab.ProtectedCanonicalRoot?.ToArray(),
                lab.IsTooltailOwnedLab)
            : Failure(stored.FailureCode!);
    }

    public async Task<SafeLabGrantResult> RevokeStoredAsync(
        LocalFolderGrantStateRecord storedGrant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storedGrant);
        LocalFolderGrant grant = storedGrant.Grant;
        StateReadResult<FileSkillWorkspaceStateRecord> workspace =
            await stateStore.LoadWorkspaceStateAsync(
                grant.CompanionId,
                cancellationToken).ConfigureAwait(false);
        if (!workspace.IsSuccess)
        {
            return Failure(workspace.ReasonCode);
        }

        LocalFolderGrantStateRecord? current = workspace.Value!.Grants
            .SingleOrDefault(candidate => candidate.Grant.Id == grant.Id);
        if (current is null || current.Grant.RootIdentity != grant.RootIdentity ||
            !ProtectedRootsEqual(
                current.ProtectedCanonicalRoot,
                storedGrant.ProtectedCanonicalRoot))
        {
            return Failure("folder_grant.persisted_identity_changed");
        }

        Tooltail.Domain.Common.DomainResult<LocalFolderGrant> revoked =
            current.Grant.Revoke(clock.UtcNow, "user_revoked");
        if (!revoked.IsSuccess)
        {
            return Failure(revoked.Error!.Code);
        }

        StateWriteResult stored = await stateStore.StoreLocalFolderGrantAsync(
            new LocalFolderGrantStateRecord(
                revoked.Value!,
                current.ProtectedCanonicalRoot?.ToArray()),
            cancellationToken).ConfigureAwait(false);
        return stored.IsSuccess
            ? new SafeLabGrantResult(
                true,
                "folder_grant.unavailable_root_revoked",
                revoked.Value,
                null,
                null,
                current.ProtectedCanonicalRoot?.ToArray(),
                IsTooltailOwnedLab: current.ProtectedCanonicalRoot is null)
            : Failure(stored.FailureCode!);
    }

    private static bool ProtectedRootsEqual(byte[]? left, byte[]? right) =>
        left is null
            ? right is null
            : right is not null && left.AsSpan().SequenceEqual(right);

    private PathSafetyResult<CanonicalLocalRoot> EnsureOwnedDirectory(
        CanonicalLocalRoot parent,
        string relativePath)
    {
        PathSafetyResult<BoundLocalPath> existing = pathSafety.Bind(
            parent,
            relativePath,
            PathEntryExpectation.MayExist);
        if (!existing.IsSuccess)
        {
            return PathSafetyResult.Failure<CanonicalLocalRoot>(
                existing.Error!.Code,
                existing.Error.Message);
        }

        if (existing.Value!.Components[^1].Existed)
        {
            return pathSafety.CaptureSubroot(parent, relativePath);
        }

        PathSafetyResult<BoundLocalPath> current = pathSafety.Revalidate(existing.Value);
        if (!current.IsSuccess)
        {
            return PathSafetyResult.Failure<CanonicalLocalRoot>(
                current.Error!.Code,
                current.Error.Message);
        }

        Directory.CreateDirectory(current.Value!.FullPath);
        return pathSafety.CaptureSubroot(parent, relativePath);
    }

    private PathSafetyResult<CanonicalLocalRoot> EnsureNewOwnedDirectory(
        CanonicalLocalRoot parent,
        string relativePath)
    {
        PathSafetyResult<BoundLocalPath> destination = pathSafety.Bind(
            parent,
            relativePath,
            PathEntryExpectation.MustNotExist);
        if (!destination.IsSuccess)
        {
            return PathSafetyResult.Failure<CanonicalLocalRoot>(
                destination.Error!.Code,
                destination.Error.Message);
        }

        PathSafetyResult<BoundLocalPath> current = pathSafety.Revalidate(destination.Value!);
        if (!current.IsSuccess)
        {
            return PathSafetyResult.Failure<CanonicalLocalRoot>(
                current.Error!.Code,
                current.Error.Message);
        }

        Directory.CreateDirectory(current.Value!.FullPath);
        return pathSafety.CaptureSubroot(parent, relativePath);
    }

    private static SafeLabGrantResult Failure(string reasonCode) =>
        new(false, reasonCode, null, null, null);
}
