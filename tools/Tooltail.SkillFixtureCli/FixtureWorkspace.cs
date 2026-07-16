using System.Text.Json;

namespace Tooltail.SkillFixtureCli;

internal sealed record FixtureWorkspaceManifest
{
    public required string ContractVersion { get; init; }

    public required Guid WorkspaceId { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public required DateTimeOffset BaseUtc { get; init; }

    public required string SkillName { get; init; }

    public required string SkillDescription { get; init; }
}

internal sealed record FixtureWorkspaceResult(
    bool IsSuccess,
    string ReasonCode,
    FixtureWorkspace? Workspace)
{
    public static FixtureWorkspaceResult Success(FixtureWorkspace workspace) =>
        new(true, "fixture.workspace_ready", workspace);

    public static FixtureWorkspaceResult Failure(string reasonCode) =>
        new(false, reasonCode, null);
}

internal sealed class FixtureWorkspace
{
    public const string ContractVersion = "tooltail.fixture-workspace/1";
    public const int MaximumArtifactBytes = 4 * 1024 * 1024;

    private const string MarkerName = ".tooltail-fixture.json";

    private FixtureWorkspace(string path, FixtureWorkspaceManifest manifest)
    {
        Path = path;
        Manifest = manifest;
    }

    public string Path { get; }

    public FixtureWorkspaceManifest Manifest { get; }

    public string RootPath => System.IO.Path.Combine(Path, "root");

    public string ArtifactPath => System.IO.Path.Combine(Path, "artifacts");

    public string StatePath => System.IO.Path.Combine(Path, "state");

    public string TemporaryPath => System.IO.Path.Combine(Path, "temp");

    public string DatabasePath => System.IO.Path.Combine(StatePath, "tooltail.db");

    public DateTimeOffset AtMinute(int minute) => Manifest.BaseUtc.AddMinutes(minute);

    public Guid Id(string label) => FixtureIdentity.Derive(Manifest.WorkspaceId, label);

    public string Artifact(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.IndexOfAny(['/', '\\']) >= 0 ||
            fileName is "." or "..")
        {
            throw new ArgumentException("Artifact names must be fixed single path segments.", nameof(fileName));
        }

        return System.IO.Path.Combine(ArtifactPath, fileName);
    }

    public static async Task<FixtureWorkspaceResult> CreateAsync(
        string requestedPath,
        Guid workspaceId,
        DateTimeOffset createdUtc,
        string skillName,
        string skillDescription,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeNewPath(requestedPath, out string? fullPath, out string? failure))
        {
            return FixtureWorkspaceResult.Failure(failure!);
        }

        if (workspaceId == Guid.Empty ||
            createdUtc.Offset != TimeSpan.Zero ||
            !IsBoundedText(skillName, 80) ||
            !IsBoundedText(skillDescription, 400))
        {
            return FixtureWorkspaceResult.Failure("fixture.workspace_manifest_invalid");
        }

        FixtureWorkspaceManifest manifest = new()
        {
            ContractVersion = ContractVersion,
            WorkspaceId = workspaceId,
            CreatedUtc = createdUtc,
            BaseUtc = createdUtc,
            SkillName = skillName,
            SkillDescription = skillDescription,
        };
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(fullPath!);
            Directory.CreateDirectory(System.IO.Path.Combine(fullPath!, "root"));
            Directory.CreateDirectory(System.IO.Path.Combine(fullPath!, "artifacts"));
            Directory.CreateDirectory(System.IO.Path.Combine(fullPath!, "state"));
            Directory.CreateDirectory(System.IO.Path.Combine(fullPath!, "temp"));
            string marker = System.IO.Path.Combine(fullPath!, MarkerName);
            await using FileStream stream = new(
                marker,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(FixtureJson.Serialize(manifest), cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            FixtureWorkspace workspace = new(fullPath!, manifest);
            return ValidateOwnedLayout(workspace)
                ? FixtureWorkspaceResult.Success(workspace)
                : FixtureWorkspaceResult.Failure("fixture.workspace_layout_invalid");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return FixtureWorkspaceResult.Failure("fixture.workspace_access_denied");
        }
        catch (IOException)
        {
            return FixtureWorkspaceResult.Failure("fixture.workspace_io_failure");
        }
    }

    public static async Task<FixtureWorkspaceResult> OpenAsync(
        string requestedPath,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeExistingPath(requestedPath, out string? fullPath, out string? failure))
        {
            return FixtureWorkspaceResult.Failure(failure!);
        }

        string marker = System.IO.Path.Combine(fullPath!, MarkerName);
        try
        {
            FileInfo markerInfo = new(marker);
            markerInfo.Refresh();
            if (!markerInfo.Exists ||
                markerInfo.Length is < 2 or > 64 * 1024 ||
                IsReparse(markerInfo))
            {
                return FixtureWorkspaceResult.Failure("fixture.workspace_marker_invalid");
            }

            byte[] bytes = await ReadBoundedAsync(
                marker,
                maximumBytes: 64 * 1024,
                cancellationToken).ConfigureAwait(false);
            FixtureWorkspaceManifest? manifest = FixtureJson.Deserialize<FixtureWorkspaceManifest>(
                bytes);
            if (!IsValidManifest(manifest))
            {
                return FixtureWorkspaceResult.Failure("fixture.workspace_manifest_invalid");
            }

            FixtureWorkspace workspace = new(fullPath!, manifest!);
            return ValidateOwnedLayout(workspace)
                ? FixtureWorkspaceResult.Success(workspace)
                : FixtureWorkspaceResult.Failure("fixture.workspace_layout_invalid");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException)
        {
            return FixtureWorkspaceResult.Failure("fixture.workspace_manifest_invalid");
        }
        catch (UnauthorizedAccessException)
        {
            return FixtureWorkspaceResult.Failure("fixture.workspace_access_denied");
        }
        catch (IOException)
        {
            return FixtureWorkspaceResult.Failure("fixture.workspace_io_failure");
        }
    }

    public async Task WriteArtifactAsync(
        string fileName,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        if (bytes.Length is < 2 or > MaximumArtifactBytes)
        {
            throw new ArgumentException("Fixture artifacts must remain within the closed size bound.", nameof(bytes));
        }

        string target = Artifact(fileName);
        if (!IsSafeOwnedFileSlot(ArtifactPath, target))
        {
            throw new IOException("The fixture artifact target is not a safe owned file slot.");
        }

        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (!IsSafeOwnedFileSlot(ArtifactPath, temporary) || File.Exists(temporary))
            {
                throw new IOException("The fixture artifact temporary slot is not safe.");
            }

            await using (FileStream stream = new(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!IsSafeOwnedFileSlot(ArtifactPath, temporary) ||
                !IsSafeOwnedFileSlot(ArtifactPath, target))
            {
                throw new IOException("The fixture artifact path changed during the write.");
            }

            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary) && IsSafeOwnedFileSlot(ArtifactPath, temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public Task<byte[]> ReadArtifactAsync(
        string fileName,
        CancellationToken cancellationToken = default)
    {
        string target = Artifact(fileName);
        if (!IsSafeOwnedRegularFile(ArtifactPath, target))
        {
            throw new IOException("The fixture artifact is missing or unsafe.");
        }

        return ReadBoundedAsync(target, MaximumArtifactBytes, cancellationToken);
    }

    public bool IsStateStorageSafe()
    {
        if (!ValidateOwnedLayout(this))
        {
            return false;
        }

        string[] stateFiles =
        [
            DatabasePath,
            DatabasePath + "-shm",
            DatabasePath + "-wal",
            DatabasePath + "-journal",
        ];
        return stateFiles.All(path => IsSafeOwnedFileSlot(StatePath, path));
    }

    private static bool IsValidManifest(FixtureWorkspaceManifest? manifest) =>
        manifest is not null &&
        manifest.ContractVersion == ContractVersion &&
        manifest.WorkspaceId != Guid.Empty &&
        manifest.CreatedUtc.Offset == TimeSpan.Zero &&
        manifest.BaseUtc.Offset == TimeSpan.Zero &&
        manifest.BaseUtc == manifest.CreatedUtc &&
        IsBoundedText(manifest.SkillName, 80) &&
        IsBoundedText(manifest.SkillDescription, 400);

    private static bool ValidateOwnedLayout(FixtureWorkspace workspace)
    {
        if (!IsLinkFreeAncestry(workspace.Path))
        {
            return false;
        }

        string[] fixedDirectories =
        [
            workspace.RootPath,
            workspace.ArtifactPath,
            workspace.StatePath,
            workspace.TemporaryPath,
        ];
        return fixedDirectories.All(path =>
            IsDirectChild(workspace.Path, path) && IsLinkFreeDirectory(path));
    }

    private static bool TryNormalizeNewPath(
        string requestedPath,
        out string? fullPath,
        out string? failure)
    {
        fullPath = null;
        failure = null;
        if (!TryNormalizeAbsolutePath(requestedPath, out fullPath) ||
            Directory.Exists(fullPath) ||
            File.Exists(fullPath))
        {
            failure = "fixture.workspace_must_be_new";
            return false;
        }

        string? parent = System.IO.Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parent) || !IsLinkFreeAncestry(parent))
        {
            failure = "fixture.workspace_parent_invalid";
            return false;
        }

        return true;
    }

    private static bool TryNormalizeExistingPath(
        string requestedPath,
        out string? fullPath,
        out string? failure)
    {
        fullPath = null;
        failure = null;
        if (!TryNormalizeAbsolutePath(requestedPath, out fullPath) ||
            !Directory.Exists(fullPath) ||
            !IsLinkFreeAncestry(fullPath))
        {
            failure = "fixture.workspace_not_found";
            return false;
        }

        return true;
    }

    private static bool TryNormalizeAbsolutePath(string value, out string? fullPath)
    {
        fullPath = null;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 32_768 ||
            value.Contains('\0', StringComparison.Ordinal) ||
            !System.IO.Path.IsPathFullyQualified(value))
        {
            return false;
        }

        try
        {
            fullPath = System.IO.Path.GetFullPath(value);
            string? root = System.IO.Path.GetPathRoot(fullPath);
            return !string.Equals(fullPath, root, PathComparison);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsLinkFreeDirectory(string path)
    {
        try
        {
            DirectoryInfo info = new(path);
            info.Refresh();
            return info.Exists && !IsReparse(info);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static bool IsLinkFreeAncestry(string path)
    {
        try
        {
            DirectoryInfo? current = new(System.IO.Path.GetFullPath(path));
            while (current is not null)
            {
                current.Refresh();
                if (!current.Exists || IsReparse(current))
                {
                    return false;
                }

                current = current.Parent;
            }

            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or
                UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static bool IsReparse(FileSystemInfo info) =>
        (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null;

    private static bool IsSafeOwnedRegularFile(string ownerDirectory, string path)
    {
        try
        {
            FileInfo info = new(path);
            info.Refresh();
            return IsDirectChild(ownerDirectory, path) &&
                IsLinkFreeAncestry(ownerDirectory) &&
                info.Exists &&
                !IsReparse(info);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static bool IsSafeOwnedFileSlot(string ownerDirectory, string path)
    {
        try
        {
            if (!IsDirectChild(ownerDirectory, path) ||
                !IsLinkFreeAncestry(ownerDirectory) ||
                Directory.Exists(path))
            {
                return false;
            }

            FileInfo info = new(path);
            info.Refresh();
            return info.Exists ? !IsReparse(info) : info.LinkTarget is null;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static bool IsDirectChild(string parent, string child) =>
        string.Equals(
            System.IO.Path.GetDirectoryName(child),
            parent,
            PathComparison);

    private static bool IsBoundedText(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    private static async Task<byte[]> ReadBoundedAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 2 || stream.Length > maximumBytes)
        {
            throw new InvalidDataException("Fixture artifact size is outside the closed bound.");
        }

        byte[] bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
