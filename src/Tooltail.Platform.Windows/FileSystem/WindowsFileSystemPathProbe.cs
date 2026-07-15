using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Tooltail.Application.Abstractions;

namespace Tooltail.Platform.Windows.FileSystem;

public sealed partial class WindowsFileSystemPathProbe : IFileSystemPathProbe
{
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeDevice = 0x00000040;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidName = 123;
    private const int MaximumWindowsPathCharacters = 32767;

    public FileSystemPathProbeResult Probe(string absolutePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return FileSystemPathProbeResult.Failed(
                FileSystemPathProbeStatus.Unsupported,
                "filesystem.windows_required");
        }

        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            return FileSystemPathProbeResult.Failed(
                FileSystemPathProbeStatus.InvalidPath,
                "filesystem.invalid_path");
        }

        try
        {
            using SafeFileHandle handle = NativeMethods.CreateFile(
                absolutePath,
                FileReadAttributes,
                FileShareRead | FileShareWrite | FileShareDelete,
                0,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                0);
            if (handle.IsInvalid)
            {
                return FailureFromWin32(Marshal.GetLastPInvokeError());
            }

            if (!NativeMethods.GetFileInformationByHandle(handle, out ByHandleFileInformation information))
            {
                return FailureFromWin32(Marshal.GetLastPInvokeError());
            }

            string? canonicalPath = GetCanonicalPath(handle);
            if (canonicalPath is null)
            {
                return FileSystemPathProbeResult.Failed(
                    FileSystemPathProbeStatus.IoFailure,
                    "filesystem.canonical_path_failed");
            }

            bool isDirectory = (information.FileAttributes & FileAttributeDirectory) != 0;
            bool isDevice = (information.FileAttributes & FileAttributeDevice) != 0;
            bool isReparsePoint = (information.FileAttributes & FileAttributeReparsePoint) != 0;
            FileSystemEntryKind kind = isDirectory
                ? FileSystemEntryKind.Directory
                : isDevice
                    ? FileSystemEntryKind.Other
                    : FileSystemEntryKind.File;
            string volumeIdentity = $"win32-volume:{information.VolumeSerialNumber:x8}";
            string entryIdentity =
                $"win32-file:{information.FileIndexHigh:x8}{information.FileIndexLow:x8}";

            return FileSystemPathProbeResult.Found(
                canonicalPath,
                kind,
                volumeIdentity,
                entryIdentity,
                isReparsePoint,
                IsLocalFixedDrive(canonicalPath));
        }
        catch (ArgumentException)
        {
            return FileSystemPathProbeResult.Failed(
                FileSystemPathProbeStatus.InvalidPath,
                "filesystem.invalid_path");
        }
        catch (NotSupportedException)
        {
            return FileSystemPathProbeResult.Failed(
                FileSystemPathProbeStatus.Unsupported,
                "filesystem.path_unsupported");
        }
        catch (IOException)
        {
            return FileSystemPathProbeResult.Failed(
                FileSystemPathProbeStatus.IoFailure,
                "filesystem.io_failure");
        }
        catch (UnauthorizedAccessException)
        {
            return FileSystemPathProbeResult.Failed(
                FileSystemPathProbeStatus.AccessDenied,
                "filesystem.access_denied");
        }
    }

    private static FileSystemPathProbeResult FailureFromWin32(int errorCode) =>
        errorCode switch
        {
            ErrorFileNotFound or ErrorPathNotFound => FileSystemPathProbeResult.Failed(
                FileSystemPathProbeStatus.NotFound,
                "filesystem.not_found"),
            ErrorAccessDenied => FileSystemPathProbeResult.Failed(
                FileSystemPathProbeStatus.AccessDenied,
                "filesystem.access_denied"),
            ErrorInvalidName => FileSystemPathProbeResult.Failed(
                FileSystemPathProbeStatus.InvalidPath,
                "filesystem.invalid_path"),
            _ => FileSystemPathProbeResult.Failed(
                FileSystemPathProbeStatus.IoFailure,
                "filesystem.io_failure"),
        };

    private static string? GetCanonicalPath(SafeFileHandle handle)
    {
        char[] buffer = new char[512];
        uint length = NativeMethods.GetFinalPathNameByHandle(
            handle,
            buffer,
            (uint)buffer.Length,
            0);
        if (length == 0)
        {
            return null;
        }

        if (length >= buffer.Length)
        {
            if (length > MaximumWindowsPathCharacters)
            {
                return null;
            }

            buffer = new char[length + 1];
            length = NativeMethods.GetFinalPathNameByHandle(
                handle,
                buffer,
                (uint)buffer.Length,
                0);
            if (length == 0 || length >= buffer.Length)
            {
                return null;
            }
        }

        string path = new(buffer, 0, checked((int)length));
        if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
        {
            return $"\\\\{path[8..]}";
        }

        return path.StartsWith("\\\\?\\", StringComparison.Ordinal)
            ? path[4..]
            : path;
    }

    private static bool IsLocalFixedDrive(string canonicalPath)
    {
        if (canonicalPath.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return false;
        }

        string? root = Path.GetPathRoot(canonicalPath);
        return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Fixed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private static partial class NativeMethods
    {
        [LibraryImport(
            "kernel32.dll",
            EntryPoint = "CreateFileW",
            SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        internal static partial SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            nint securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            nint templateFile);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation fileInformation);

        [LibraryImport(
            "kernel32.dll",
            EntryPoint = "GetFinalPathNameByHandleW",
            SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        internal static partial uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            [Out] char[] filePath,
            uint filePathLength,
            uint flags);
    }
}
