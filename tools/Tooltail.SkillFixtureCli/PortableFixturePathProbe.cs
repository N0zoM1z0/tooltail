using System.Security.Cryptography;
using System.Text;
using Tooltail.Application.Abstractions;

namespace Tooltail.SkillFixtureCli;

internal sealed class PortableFixturePathProbe : IFileSystemPathProbe
{
    private const long MaximumIdentityHashBytes = 16 * 1024 * 1024;

    private readonly string workspacePath;
    private readonly string rootPath;
    private readonly string temporaryPath;
    private readonly string volumeIdentity;
    private readonly Guid workspaceId;

    public PortableFixturePathProbe(FixtureWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        workspacePath = Path.GetFullPath(workspace.Path);
        rootPath = Path.GetFullPath(workspace.RootPath);
        temporaryPath = Path.GetFullPath(workspace.TemporaryPath);
        workspaceId = workspace.Manifest.WorkspaceId;
        volumeIdentity = $"fixture-volume:{workspaceId:D}";
    }

    public FileSystemPathProbeResult Probe(string absolutePath)
    {
        try
        {
            string fullPath = Path.GetFullPath(absolutePath);
            FileSystemInfo? info = Directory.Exists(fullPath)
                ? new DirectoryInfo(fullPath)
                : File.Exists(fullPath)
                    ? new FileInfo(fullPath)
                    : null;
            if (info is null)
            {
                return FileSystemPathProbeResult.Failed(
                    FileSystemPathProbeStatus.NotFound,
                    "fixture.probe_not_found");
            }

            info.Refresh();
            bool isDirectory = info is DirectoryInfo;
            bool isReparse =
                (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                info.LinkTarget is not null;
            string identity = isDirectory
                ? DirectoryIdentity(fullPath)
                : FileIdentity((FileInfo)info);
            return FileSystemPathProbeResult.Found(
                fullPath,
                isDirectory ? FileSystemEntryKind.Directory : FileSystemEntryKind.File,
                volumeIdentity,
                identity,
                isReparse,
                isLocalFixedDrive: true);
        }
        catch (UnauthorizedAccessException)
        {
            return FileSystemPathProbeResult.Failed(
                FileSystemPathProbeStatus.AccessDenied,
                "fixture.probe_access_denied");
        }
        catch (Exception exception) when (
            exception is IOException or NotSupportedException or ArgumentException)
        {
            return FileSystemPathProbeResult.Failed(
                FileSystemPathProbeStatus.IoFailure,
                "fixture.probe_failed");
        }
    }

    private string DirectoryIdentity(string fullPath)
    {
        if (string.Equals(fullPath, rootPath, PathComparison))
        {
            return $"fixture-root:{workspaceId:D}";
        }

        if (string.Equals(fullPath, temporaryPath, PathComparison))
        {
            return $"fixture-temp:{workspaceId:D}";
        }

        string material = IsInsideWorkspace(fullPath)
            ? Path.GetRelativePath(workspacePath, fullPath).Replace(Path.DirectorySeparatorChar, '/')
            : fullPath;
        return "fixture-directory:" + Hash(Encoding.UTF8.GetBytes(material));
    }

    private static string FileIdentity(FileInfo file)
    {
        if (file.Length > MaximumIdentityHashBytes)
        {
            throw new IOException("Fixture files exceed the identity hashing bound.");
        }

        using FileStream stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        return "fixture-file:" + Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private bool IsInsideWorkspace(string fullPath)
    {
        string relative = Path.GetRelativePath(workspacePath, fullPath);
        return relative == "." ||
            (relative != ".." &&
             !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
             !Path.IsPathRooted(relative));
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
