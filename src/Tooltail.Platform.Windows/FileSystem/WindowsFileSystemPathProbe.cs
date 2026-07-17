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
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidName = 123;

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

            if (!WindowsFileHandleInspector.TryInspect(
                    handle,
                    includeCanonicalPath: true,
                    out WindowsFileHandleSnapshot? information,
                    out int inspectionError))
            {
                return FailureFromWin32(inspectionError);
            }

            return FileSystemPathProbeResult.Found(
                information!.CanonicalPath!,
                information.Kind,
                information.VolumeIdentity,
                information.EntryIdentity,
                information.IsReparsePoint,
                WindowsFileHandleInspector.IsLocalFixedDrive(information.CanonicalPath!));
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

    }
}
