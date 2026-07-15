namespace Tooltail.Application.Abstractions;

/// <summary>
/// Defines the read-only file-system surface used while discovering and snapshotting resources.
/// This interface grants no mutation authority.
/// </summary>
public interface IFileSystemMetadataReader
{
    bool DirectoryExists(string fullPath);

    bool FileExists(string fullPath);

    IReadOnlyList<string> EnumerateFileSystemEntries(string fullPath);
}
