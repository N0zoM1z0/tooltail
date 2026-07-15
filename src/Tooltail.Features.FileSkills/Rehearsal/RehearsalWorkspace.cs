using System.Collections.Concurrent;
using Tooltail.Application.Abstractions;
using Tooltail.Features.FileSkills.Paths;

namespace Tooltail.Features.FileSkills.Rehearsal;

public sealed record RehearsalWorkspace
{
    internal RehearsalWorkspace(
        Guid ownershipToken,
        string relativePath,
        CanonicalLocalRoot root)
    {
        if (ownershipToken == Guid.Empty)
        {
            throw new ArgumentException("A rehearsal workspace requires a non-empty ownership token.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(root);
        OwnershipToken = ownershipToken;
        RelativePath = relativePath;
        Root = root;
    }

    internal Guid OwnershipToken { get; }

    internal string RelativePath { get; }

    public CanonicalLocalRoot Root { get; }
}

public sealed record RehearsalWorkspaceResult
{
    private RehearsalWorkspaceResult(
        bool isSuccess,
        string reasonCode,
        RehearsalWorkspace? workspace)
    {
        IsSuccess = isSuccess;
        ReasonCode = reasonCode;
        Workspace = workspace;
    }

    public bool IsSuccess { get; }

    public string ReasonCode { get; }

    public RehearsalWorkspace? Workspace { get; }

    internal static RehearsalWorkspaceResult Success(RehearsalWorkspace workspace) =>
        new(true, "rehearsal.workspace_created", workspace);

    internal static RehearsalWorkspaceResult Failure(string reasonCode) =>
        new(false, reasonCode, null);
}

public sealed record RehearsalCleanupResult(bool IsSuccess, string ReasonCode);

public interface IRehearsalWorkspaceFactory
{
    CanonicalLocalRoot OwnedTemporaryRoot { get; }

    ValueTask<RehearsalWorkspaceResult> CreateAsync(
        CancellationToken cancellationToken = default);

    ValueTask<RehearsalCleanupResult> CleanupAsync(
        RehearsalWorkspace workspace,
        CancellationToken cancellationToken = default);
}

public sealed class TooltailOwnedRehearsalWorkspaceFactory : IRehearsalWorkspaceFactory
{
    private readonly CanonicalLocalRoot ownedTemporaryRoot;
    private readonly WindowsPathSafetyService pathSafety;
    private readonly IIdGenerator idGenerator;
    private readonly int maximumCleanupEntries;
    private readonly ConcurrentDictionary<Guid, WorkspaceRegistration> registrations = new();

    public TooltailOwnedRehearsalWorkspaceFactory(
        CanonicalLocalRoot ownedTemporaryRoot,
        WindowsPathSafetyService pathSafety,
        IIdGenerator idGenerator,
        int maximumCleanupEntries = 10_000)
    {
        ArgumentNullException.ThrowIfNull(ownedTemporaryRoot);
        ArgumentNullException.ThrowIfNull(pathSafety);
        ArgumentNullException.ThrowIfNull(idGenerator);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCleanupEntries, 1);
        this.ownedTemporaryRoot = ownedTemporaryRoot;
        this.pathSafety = pathSafety;
        this.idGenerator = idGenerator;
        this.maximumCleanupEntries = maximumCleanupEntries;
    }

    public CanonicalLocalRoot OwnedTemporaryRoot => ownedTemporaryRoot;

    public ValueTask<RehearsalWorkspaceResult> CreateAsync(
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(
                RehearsalWorkspaceResult.Failure("rehearsal.cancelled"));
        }

        Guid ownershipToken = idGenerator.NewId();
        if (ownershipToken == Guid.Empty)
        {
            return ValueTask.FromResult(
                RehearsalWorkspaceResult.Failure("rehearsal.workspace_id_invalid"));
        }

        string relativePath = $"rehearsal-{ownershipToken:N}";
        PathSafetyResult<BoundLocalPath> destination = pathSafety.Bind(
            ownedTemporaryRoot,
            relativePath,
            PathEntryExpectation.MustNotExist);
        if (!destination.IsSuccess)
        {
            return ValueTask.FromResult(
                RehearsalWorkspaceResult.Failure(destination.Error!.Code));
        }

        try
        {
            Directory.CreateDirectory(destination.Value!.FullPath);
            PathSafetyResult<CanonicalLocalRoot> captured = pathSafety.CaptureSubroot(
                ownedTemporaryRoot,
                relativePath);
            if (!captured.IsSuccess)
            {
                return ValueTask.FromResult(
                    RehearsalWorkspaceResult.Failure(captured.Error!.Code));
            }

            RehearsalWorkspace workspace = new(
                ownershipToken,
                relativePath,
                captured.Value!);
            if (!registrations.TryAdd(
                    ownershipToken,
                    new WorkspaceRegistration(relativePath, captured.Value!)))
            {
                return ValueTask.FromResult(
                    RehearsalWorkspaceResult.Failure("rehearsal.workspace_id_collision"));
            }

            return ValueTask.FromResult(RehearsalWorkspaceResult.Success(workspace));
        }
        catch (UnauthorizedAccessException)
        {
            return ValueTask.FromResult(
                RehearsalWorkspaceResult.Failure("rehearsal.workspace_access_denied"));
        }
        catch (IOException)
        {
            return ValueTask.FromResult(
                RehearsalWorkspaceResult.Failure("rehearsal.workspace_io_failure"));
        }
    }

    public ValueTask<RehearsalCleanupResult> CleanupAsync(
        RehearsalWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(
                new RehearsalCleanupResult(false, "rehearsal.cleanup_cancelled"));
        }

        if (!registrations.TryGetValue(
                workspace.OwnershipToken,
                out WorkspaceRegistration? registration) ||
            !string.Equals(
                registration.RelativePath,
                workspace.RelativePath,
                StringComparison.Ordinal) ||
            registration.Root.Identity != workspace.Root.Identity ||
            !string.Equals(
                registration.Root.CanonicalPath,
                workspace.Root.CanonicalPath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            return ValueTask.FromResult(
                new RehearsalCleanupResult(false, "rehearsal.workspace_not_owned"));
        }

        PathSafetyResult<CanonicalLocalRoot> current = pathSafety.CaptureSubroot(
            ownedTemporaryRoot,
            registration.RelativePath);
        if (!current.IsSuccess || current.Value!.Identity != registration.Root.Identity)
        {
            return ValueTask.FromResult(
                new RehearsalCleanupResult(false, "rehearsal.workspace_identity_changed"));
        }

        try
        {
            if (!TreeIsBoundedAndLinkFree(current.Value.CanonicalPath, cancellationToken))
            {
                return ValueTask.FromResult(
                    new RehearsalCleanupResult(false, "rehearsal.workspace_cleanup_unsafe"));
            }

            Directory.Delete(current.Value.CanonicalPath, recursive: true);
            registrations.TryRemove(workspace.OwnershipToken, out _);
            return ValueTask.FromResult(
                new RehearsalCleanupResult(true, "rehearsal.workspace_removed"));
        }
        catch (OperationCanceledException)
        {
            return ValueTask.FromResult(
                new RehearsalCleanupResult(false, "rehearsal.cleanup_cancelled"));
        }
        catch (UnauthorizedAccessException)
        {
            return ValueTask.FromResult(
                new RehearsalCleanupResult(false, "rehearsal.cleanup_access_denied"));
        }
        catch (IOException)
        {
            return ValueTask.FromResult(
                new RehearsalCleanupResult(false, "rehearsal.cleanup_io_failure"));
        }
    }

    private bool TreeIsBoundedAndLinkFree(
        string root,
        CancellationToken cancellationToken)
    {
        int entryCount = 0;
        Queue<DirectoryInfo> pending = new();
        pending.Enqueue(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectoryInfo directory = pending.Dequeue();
            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos(
                         "*",
                         new EnumerationOptions
                         {
                             AttributesToSkip = 0,
                             IgnoreInaccessible = false,
                             RecurseSubdirectories = false,
                             ReturnSpecialDirectories = false,
                         }))
            {
                entryCount++;
                if (entryCount > maximumCleanupEntries ||
                    (entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    entry.LinkTarget is not null)
                {
                    return false;
                }

                if (entry is DirectoryInfo child)
                {
                    pending.Enqueue(child);
                }
            }
        }

        return true;
    }

    private sealed record WorkspaceRegistration(
        string RelativePath,
        CanonicalLocalRoot Root);
}
