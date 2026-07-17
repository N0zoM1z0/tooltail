using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Tooltail.Application.Abstractions;

namespace Tooltail.Platform.Windows.FileSystem;

internal sealed record WindowsFileHandleSnapshot(
    FileSystemEntryKind Kind,
    string VolumeIdentity,
    string EntryIdentity,
    bool IsReparsePoint,
    uint Attributes,
    long CreationTime,
    long LastAccessTime,
    long LastWriteTime,
    long Length,
    string? CanonicalPath);

internal static partial class WindowsFileHandleInspector
{
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeDevice = 0x00000040;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const int FileIdInfoClass = 18;
    private const int MaximumWindowsPathCharacters = 32767;

    public static bool TryInspect(
        SafeFileHandle handle,
        bool includeCanonicalPath,
        out WindowsFileHandleSnapshot? snapshot,
        out int errorCode)
    {
        ArgumentNullException.ThrowIfNull(handle);
        snapshot = null;
        if (handle.IsInvalid || handle.IsClosed ||
            !NativeMethods.GetFileInformationByHandle(handle, out ByHandleFileInformation basic))
        {
            errorCode = Marshal.GetLastPInvokeError();
            return false;
        }

        if (!NativeMethods.GetFileInformationByHandleEx(
                handle,
                FileIdInfoClass,
                out FileIdInformation identity,
                (uint)Marshal.SizeOf<FileIdInformation>()))
        {
            errorCode = Marshal.GetLastPInvokeError();
            return false;
        }

        string? canonicalPath = includeCanonicalPath ? GetCanonicalPath(handle) : null;
        if (includeCanonicalPath && canonicalPath is null)
        {
            errorCode = Marshal.GetLastPInvokeError();
            return false;
        }

        bool isDirectory = (basic.FileAttributes & FileAttributeDirectory) != 0;
        bool isDevice = (basic.FileAttributes & FileAttributeDevice) != 0;
        FileSystemEntryKind kind = isDirectory
            ? FileSystemEntryKind.Directory
            : isDevice
                ? FileSystemEntryKind.Other
                : FileSystemEntryKind.File;
        string entryIdentity;
        unsafe
        {
            entryIdentity = $"win32-file-v2:{Convert.ToHexStringLower(
                new ReadOnlySpan<byte>(identity.FileIdentifier, 16))}";
        }

        snapshot = new WindowsFileHandleSnapshot(
            kind,
            $"win32-volume-v2:{identity.VolumeSerialNumber:x16}",
            entryIdentity,
            (basic.FileAttributes & FileAttributeReparsePoint) != 0,
            basic.FileAttributes,
            basic.CreationTime.ToInt64(),
            basic.LastAccessTime.ToInt64(),
            basic.LastWriteTime.ToInt64(),
            checked(((long)basic.FileSizeHigh << 32) | basic.FileSizeLow),
            canonicalPath);
        errorCode = 0;
        return true;
    }

    public static bool IsLocalFixedDrive(string canonicalPath)
    {
        if (canonicalPath.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return false;
        }

        string? root = Path.GetPathRoot(canonicalPath);
        return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Fixed;
    }

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

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct NativeFileTime
    {
        private readonly uint lowDateTime;
        private readonly uint highDateTime;

        public long ToInt64() =>
            unchecked(((long)highDateTime << 32) | lowDateTime);
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct FileIdInformation
    {
        public ulong VolumeSerialNumber;
        public fixed byte FileIdentifier[16];
    }

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation fileInformation);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetFileInformationByHandleEx(
            SafeFileHandle file,
            int fileInformationClass,
            out FileIdInformation fileInformation,
            uint bufferSize);

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
