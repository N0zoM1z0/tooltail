using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Tooltail.Application.Abstractions;

namespace Tooltail.Platform.Windows.FileSystem;

public sealed partial class WindowsHandleBoundFileMutationEngine : IFileMutationEngine
{
    private const uint DeleteAccess = 0x00010000;
    private const uint SynchronizeAccess = 0x00100000;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileReadData = 0x00000001;
    private const uint FileWriteData = 0x00000002;
    private const uint FileTraverse = 0x00000020;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileWriteAttributes = 0x00000100;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileOpen = 1;
    private const uint FileCreate = 2;
    private const uint FileCreated = 2;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileSequentialOnly = 0x00000004;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint ObjCaseInsensitive = 0x00000040;
    private const int FileBasicInfoClass = 0;
    private const int FileRenameInfoClass = 3;
    private const int FileDispositionInfoClass = 4;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorNotSameDevice = 17;
    private const int ErrorSharingViolation = 32;
    private const int ErrorFileExists = 80;
    private const int ErrorInvalidName = 123;
    private const int ErrorDirectoryNotEmpty = 145;
    private const int ErrorAlreadyExists = 183;
    private const int CopyBufferSize = 64 * 1024;
    private const FileAttributes RetainedAttributes =
        FileAttributes.ReadOnly |
        FileAttributes.Hidden |
        FileAttributes.System |
        FileAttributes.Archive |
        FileAttributes.Temporary |
        FileAttributes.Offline |
        FileAttributes.Encrypted |
        FileAttributes.Compressed |
        FileAttributes.SparseFile |
        FileAttributes.ReparsePoint;

    private readonly IWindowsFileMutationBoundaryHook boundaryHook;

    public WindowsHandleBoundFileMutationEngine()
        : this(NoWindowsFileMutationBoundaryHook.Instance)
    {
    }

    internal WindowsHandleBoundFileMutationEngine(
        IWindowsFileMutationBoundaryHook boundaryHook)
    {
        ArgumentNullException.ThrowIfNull(boundaryHook);
        this.boundaryHook = boundaryHook;
    }

    public FileMutationPreparationResult Prepare(FileMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsWindows())
        {
            return FileMutationPreparationResult.Failure(
                FileMutationFailureKind.UnsupportedPlatform);
        }

        if (!TryParseRelativePath(request.SourceRelativePath, allowNull: true, out string[]? source) ||
            !TryParseRelativePath(
                request.DestinationRelativePath,
                allowNull: true,
                out string[]? destination))
        {
            return FileMutationPreparationResult.Failure(
                FileMutationFailureKind.InvalidRequest);
        }

        HandleLease handles = new();
        bool transferred = false;
        try
        {
            FileMutationPreparationResult result = PrepareLocked(
                request,
                source,
                destination,
                handles);
            transferred = result.IsSuccess;
            return result;
        }
        catch (UnauthorizedAccessException)
        {
            return FileMutationPreparationResult.Failure(
                FileMutationFailureKind.AccessDenied);
        }
        catch (IOException)
        {
            return FileMutationPreparationResult.Failure(
                FileMutationFailureKind.IoFailure);
        }
        catch (ArgumentException)
        {
            return FileMutationPreparationResult.Failure(
                FileMutationFailureKind.InvalidRequest);
        }
        finally
        {
            if (!transferred)
            {
                handles.Dispose();
            }
        }
    }

    private FileMutationPreparationResult PrepareLocked(
        FileMutationRequest request,
        string[]? source,
        string[]? destination,
        HandleLease handles)
    {
        FileMutationResult rootResult = OpenAndValidateRoot(
            request.Root,
            handles,
            out SafeFileHandle? rootHandle);
        if (!rootResult.IsSuccess)
        {
            return PreparationFailure(rootResult);
        }

        return request.Kind switch
        {
            FileMutationKind.CreateDirectory => PrepareCreateDirectory(
                request,
                rootHandle!,
                destination!,
                handles),
            FileMutationKind.MoveFile => PrepareMoveFile(
                request,
                rootHandle!,
                source!,
                destination!,
                handles),
            FileMutationKind.CopyFile => PrepareCopyFile(
                request,
                rootHandle!,
                source!,
                destination!,
                handles),
            FileMutationKind.RemoveCreatedFile or
                FileMutationKind.RemoveCreatedDirectory => PrepareRemoveCreatedEntry(
                    request,
                    rootHandle!,
                    source!,
                    handles),
            _ => FileMutationPreparationResult.Failure(
                FileMutationFailureKind.InvalidRequest),
        };
    }

    private FileMutationPreparationResult PrepareCreateDirectory(
        FileMutationRequest request,
        SafeFileHandle root,
        string[] destination,
        HandleLease handles)
    {
        FileMutationResult parentResult = OpenParent(
            root,
            destination,
            request.Root.VolumeIdentity,
            handles,
            out SafeFileHandle? parent);
        if (!parentResult.IsSuccess)
        {
            return PreparationFailure(parentResult);
        }

        return FileMutationPreparationResult.Success(
            new PreparedWindowsMutation(
                handles,
                () => CreateDirectoryEffect(
                    request,
                    parent!,
                    destination[^1],
                    handles)));
    }

    private FileMutationResult CreateDirectoryEffect(
        FileMutationRequest request,
        SafeFileHandle parent,
        string destinationName,
        HandleLease handles)
    {
        boundaryHook.AfterHandlesLockedBeforeEffect(request);
        FileMutationResult created = CreateRelative(
            parent,
            destinationName,
            FileListDirectory |
                FileTraverse |
                FileReadAttributes |
                DeleteAccess |
                SynchronizeAccess,
            FileDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint,
            handles,
            out SafeFileHandle? createdHandle,
            out ulong information);
        if (!created.IsSuccess)
        {
            return created;
        }

        if (information != FileCreated ||
            !TryInspectExpectedKind(
                createdHandle!,
                FileSystemEntryKind.Directory,
                request.Root.VolumeIdentity,
                out WindowsFileHandleSnapshot? snapshot))
        {
            FileMutationResult cleanup = MarkForDeletion(createdHandle!);
            return cleanup.IsSuccess
                ? FileMutationResult.Failure(FileMutationFailureKind.PathChanged)
                : FileMutationResult.Failure(
                    FileMutationFailureKind.CleanupFailed,
                    mutationMayHaveOccurred: true);
        }

        FileMutationEvidence evidence = new(
            snapshot!.VolumeIdentity,
            snapshot.EntryIdentity,
            destinationCreatedByThisCall: true);
        handles.DisposeHandle(createdHandle!);
        return FileMutationResult.Success(evidence);
    }

    private FileMutationPreparationResult PrepareMoveFile(
        FileMutationRequest request,
        SafeFileHandle root,
        string[] source,
        string[] destination,
        HandleLease handles)
    {
        FileMutationResult sourceResult = OpenAndValidateSource(
            request,
            root,
            source,
            includeDeleteAccess: true,
            includeReadData: true,
            handles,
            out SafeFileHandle? sourceHandle,
            out WindowsFileHandleSnapshot? sourceSnapshot);
        if (!sourceResult.IsSuccess)
        {
            return PreparationFailure(sourceResult);
        }

        FileMutationResult parentResult = OpenParent(
            root,
            destination,
            request.Root.VolumeIdentity,
            handles,
            out SafeFileHandle? destinationParent);
        if (!parentResult.IsSuccess)
        {
            return PreparationFailure(parentResult);
        }

        _ = destinationParent;
        return FileMutationPreparationResult.Success(
            new PreparedWindowsMutation(
                handles,
                () => MoveFileEffect(
                    request,
                    sourceHandle!,
                    sourceSnapshot!,
                    CombineRootRelativePath(request.Root.CanonicalPath, destination),
                    handles)));
    }

    private FileMutationResult MoveFileEffect(
        FileMutationRequest request,
        SafeFileHandle sourceHandle,
        WindowsFileHandleSnapshot sourceSnapshot,
        string destinationFullPath,
        HandleLease handles)
    {
        boundaryHook.AfterHandlesLockedBeforeEffect(request);
        FileMutationResult renamed = RenameToPath(
            sourceHandle,
            destinationFullPath);
        if (!renamed.IsSuccess)
        {
            return renamed;
        }

        FileMutationEvidence evidence = new(
            sourceSnapshot.VolumeIdentity,
            sourceSnapshot.EntryIdentity,
            destinationCreatedByThisCall: false);
        handles.DisposeHandle(sourceHandle);
        return FileMutationResult.Success(evidence);
    }

    private FileMutationPreparationResult PrepareCopyFile(
        FileMutationRequest request,
        SafeFileHandle root,
        string[] source,
        string[] destination,
        HandleLease handles)
    {
        FileMutationResult sourceResult = OpenAndValidateSource(
            request,
            root,
            source,
            includeDeleteAccess: false,
            includeReadData: true,
            handles,
            out SafeFileHandle? sourceHandle,
            out WindowsFileHandleSnapshot? sourceSnapshot);
        if (!sourceResult.IsSuccess)
        {
            return PreparationFailure(sourceResult);
        }

        if (sourceSnapshot!.Length > request.MaximumCopyBytes)
        {
            return FileMutationPreparationResult.Failure(
                FileMutationFailureKind.LimitExceeded);
        }

        FileMutationResult parentResult = OpenParent(
            root,
            destination,
            request.Root.VolumeIdentity,
            handles,
            out SafeFileHandle? destinationParent);
        if (!parentResult.IsSuccess)
        {
            return PreparationFailure(parentResult);
        }

        return FileMutationPreparationResult.Success(
            new PreparedWindowsMutation(
                handles,
                () => CopyFileEffect(
                    request,
                    sourceHandle!,
                    sourceSnapshot,
                    destinationParent!,
                    destination[^1],
                    handles)));
    }

    private FileMutationResult CopyFileEffect(
        FileMutationRequest request,
        SafeFileHandle sourceHandle,
        WindowsFileHandleSnapshot sourceSnapshot,
        SafeFileHandle destinationParent,
        string destinationName,
        HandleLease handles)
    {
        boundaryHook.AfterHandlesLockedBeforeEffect(request);
        FileMutationResult created = CreateRelative(
            destinationParent,
            destinationName,
            FileReadData |
                FileReadAttributes |
                FileWriteAttributes |
                FileWriteData |
                DeleteAccess |
                SynchronizeAccess,
            FileNonDirectoryFile |
                FileSequentialOnly |
                FileSynchronousIoNonAlert |
                FileOpenReparsePoint,
            handles,
            out SafeFileHandle? destinationHandle,
            out ulong information);
        if (!created.IsSuccess)
        {
            return created;
        }

        if (information != FileCreated)
        {
            return CleanupCreatedDestination(destinationHandle!, FileMutationFailureKind.PathChanged);
        }

        try
        {
            CopyContents(sourceHandle, destinationHandle!, sourceSnapshot.Length);
            RandomAccess.FlushToDisk(destinationHandle!);
            if (!SetBasicInformation(destinationHandle!, sourceSnapshot))
            {
                return CleanupCreatedDestination(
                    destinationHandle!,
                    FileMutationFailureKind.IoFailure);
            }

            if (!TryInspectExpectedKind(
                    destinationHandle!,
                    FileSystemEntryKind.File,
                    request.Root.VolumeIdentity,
                    out WindowsFileHandleSnapshot? destinationSnapshot))
            {
                return CleanupCreatedDestination(
                    destinationHandle!,
                    FileMutationFailureKind.PathChanged);
            }

            FileMutationEvidence evidence = new(
                destinationSnapshot!.VolumeIdentity,
                destinationSnapshot.EntryIdentity,
                destinationCreatedByThisCall: true);
            handles.DisposeHandle(destinationHandle!);
            return FileMutationResult.Success(evidence);
        }
        catch (IOException)
        {
            return CleanupCreatedDestination(
                destinationHandle!,
                FileMutationFailureKind.IoFailure);
        }
        catch (UnauthorizedAccessException)
        {
            return CleanupCreatedDestination(
                destinationHandle!,
                FileMutationFailureKind.AccessDenied);
        }
    }

    private FileMutationPreparationResult PrepareRemoveCreatedEntry(
        FileMutationRequest request,
        SafeFileHandle root,
        string[] source,
        HandleLease handles)
    {
        bool isFile = request.Kind == FileMutationKind.RemoveCreatedFile;
        FileMutationResult sourceResult = OpenAndValidateSource(
            request,
            root,
            source,
            includeDeleteAccess: true,
            includeReadData: isFile && request.ExpectedSource!.ContentHash is not null,
            handles,
            out SafeFileHandle? sourceHandle,
            out _);
        if (!sourceResult.IsSuccess)
        {
            return PreparationFailure(sourceResult);
        }

        return FileMutationPreparationResult.Success(
            new PreparedWindowsMutation(
                handles,
                () => RemoveCreatedEntryEffect(request, sourceHandle!, handles)));
    }

    private FileMutationResult RemoveCreatedEntryEffect(
        FileMutationRequest request,
        SafeFileHandle sourceHandle,
        HandleLease handles)
    {
        boundaryHook.AfterHandlesLockedBeforeEffect(request);
        FileMutationResult removed = MarkForDeletion(sourceHandle);
        if (!removed.IsSuccess)
        {
            return removed;
        }

        // Disposition becomes authoritative while the exact source handle is held; close it now
        // so absence can be authoritatively snapshotted while ancestry handles remain retained.
        handles.DisposeHandle(sourceHandle);
        return FileMutationResult.Success();
    }

    private static FileMutationResult OpenAndValidateRoot(
        FileMutationRootBinding expected,
        HandleLease handles,
        out SafeFileHandle? rootHandle)
    {
        rootHandle = NativeMethods.CreateFile(
            expected.CanonicalPath,
            FileListDirectory | FileTraverse | FileReadAttributes | SynchronizeAccess,
            FileShareRead | FileShareWrite,
            0,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            0);
        if (rootHandle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            rootHandle.Dispose();
            rootHandle = null;
            return FailureFromWin32(error, rootFailure: true);
        }

        handles.Add(rootHandle);
        if (!WindowsFileHandleInspector.TryInspect(
                rootHandle,
                includeCanonicalPath: true,
                out WindowsFileHandleSnapshot? actual,
                out _) ||
            actual!.Kind != FileSystemEntryKind.Directory ||
            actual.IsReparsePoint ||
            !WindowsFileHandleInspector.IsLocalFixedDrive(actual.CanonicalPath!) ||
            !PhysicalPathsEqual(actual.CanonicalPath!, expected.CanonicalPath) ||
            !string.Equals(actual.VolumeIdentity, expected.VolumeIdentity, StringComparison.Ordinal) ||
            !string.Equals(actual.EntryIdentity, expected.EntryIdentity, StringComparison.Ordinal))
        {
            return FileMutationResult.Failure(FileMutationFailureKind.RootChanged);
        }

        return FileMutationResult.Success();
    }

    private static FileMutationResult OpenAndValidateSource(
        FileMutationRequest request,
        SafeFileHandle root,
        string[] source,
        bool includeDeleteAccess,
        bool includeReadData,
        HandleLease handles,
        out SafeFileHandle? sourceHandle,
        out WindowsFileHandleSnapshot? sourceSnapshot)
    {
        sourceHandle = null;
        sourceSnapshot = null;
        FileMutationResult parentResult = OpenParent(
            root,
            source,
            request.Root.VolumeIdentity,
            handles,
            out SafeFileHandle? sourceParent);
        if (!parentResult.IsSuccess)
        {
            return parentResult;
        }

        FileMutationExpectedEntry expected = request.ExpectedSource!;
        uint desiredAccess = FileReadAttributes | SynchronizeAccess;
        if (includeDeleteAccess)
        {
            desiredAccess |= DeleteAccess;
        }

        if (includeReadData)
        {
            desiredAccess |= FileReadData;
        }

        uint options = expected.Kind == FileSystemEntryKind.File
            ? FileNonDirectoryFile | FileSequentialOnly
            : FileDirectoryFile;
        FileMutationResult opened = OpenRelative(
            sourceParent!,
            source[^1],
            desiredAccess,
            FileShareRead,
            FileOpen,
            options | FileSynchronousIoNonAlert | FileOpenReparsePoint,
            FileAttributeNormal,
            handles,
            out sourceHandle,
            out _);
        if (!opened.IsSuccess)
        {
            return opened.FailureKind == FileMutationFailureKind.DestinationExists
                ? FileMutationResult.Failure(FileMutationFailureKind.SourceChanged)
                : opened;
        }

        if (!WindowsFileHandleInspector.TryInspect(
                sourceHandle!,
                includeCanonicalPath: false,
                out sourceSnapshot,
                out _) ||
            !MatchesExpected(sourceSnapshot!, expected))
        {
            return FileMutationResult.Failure(FileMutationFailureKind.SourceChanged);
        }

        if (expected.ContentHash is not null &&
            !string.Equals(
                HashHandle(sourceHandle!, sourceSnapshot!.Length),
                expected.ContentHash,
                StringComparison.Ordinal))
        {
            return FileMutationResult.Failure(FileMutationFailureKind.SourceChanged);
        }

        return FileMutationResult.Success();
    }

    private static FileMutationResult OpenParent(
        SafeFileHandle root,
        string[] path,
        string expectedVolumeIdentity,
        HandleLease handles,
        out SafeFileHandle? parent)
    {
        parent = root;
        for (int index = 0; index < path.Length - 1; index++)
        {
            FileMutationResult opened = OpenRelative(
                parent!,
                path[index],
                FileListDirectory | FileTraverse | FileReadAttributes | SynchronizeAccess,
                FileShareRead | FileShareWrite,
                FileOpen,
                FileDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint,
                FileAttributeNormal,
                handles,
                out SafeFileHandle? child,
                out _);
            if (!opened.IsSuccess)
            {
                parent = null;
                return opened;
            }

            if (!TryInspectExpectedKind(
                    child!,
                    FileSystemEntryKind.Directory,
                    expectedVolumeIdentity,
                    out _))
            {
                parent = null;
                return FileMutationResult.Failure(FileMutationFailureKind.PathChanged);
            }

            parent = child;
        }

        return FileMutationResult.Success();
    }

    private static unsafe FileMutationResult OpenRelative(
        SafeFileHandle parent,
        string name,
        uint desiredAccess,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        uint fileAttributes,
        HandleLease handles,
        out SafeFileHandle? handle,
        out ulong information)
    {
        handle = null;
        information = 0;
        fixed (char* nameBuffer = name)
        {
            UnicodeString unicodeName = new()
            {
                Length = checked((ushort)(name.Length * sizeof(char))),
                MaximumLength = checked((ushort)(name.Length * sizeof(char))),
                Buffer = (nint)nameBuffer,
            };
            ObjectAttributes attributes = new()
            {
                Length = (uint)sizeof(ObjectAttributes),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = (nint)(&unicodeName),
                Attributes = ObjCaseInsensitive,
            };
            IoStatusBlock ioStatus = default;
            int status = NativeMethods.NtCreateFile(
                out SafeFileHandle opened,
                desiredAccess,
                &attributes,
                &ioStatus,
                null,
                fileAttributes,
                shareAccess,
                createDisposition,
                createOptions,
                null,
                0);
            if (status < 0 || opened.IsInvalid)
            {
                opened.Dispose();
                return FailureFromWin32(
                    checked((int)NativeMethods.RtlNtStatusToDosError(status)),
                    rootFailure: false);
            }

            handle = opened;
            information = (ulong)ioStatus.Information;
            handles.Add(opened);
            return FileMutationResult.Success();
        }
    }

    private static FileMutationResult CreateRelative(
        SafeFileHandle parent,
        string name,
        uint desiredAccess,
        uint createOptions,
        HandleLease handles,
        out SafeFileHandle? handle,
        out ulong information) =>
        OpenRelative(
            parent,
            name,
            desiredAccess,
            FileShareRead,
            FileCreate,
            createOptions,
            FileAttributeNormal,
            handles,
            out handle,
            out information);

    private static unsafe FileMutationResult RenameToPath(
        SafeFileHandle source,
        string destinationFullPath)
    {
        int nameBytes = checked(destinationFullPath.Length * sizeof(char));
        int rootOffset = IntPtr.Size == 8 ? 8 : 4;
        int lengthOffset = checked(rootOffset + IntPtr.Size);
        int nameOffset = checked(lengthOffset + sizeof(uint));
        int bufferSize = checked(nameOffset + nameBytes);
        byte[] buffer = new byte[bufferSize];
        fixed (byte* bufferPointer = buffer)
        fixed (char* namePointer = destinationFullPath)
        {
            *(bufferPointer) = 0;
            *(nint*)(bufferPointer + rootOffset) = 0;
            *(uint*)(bufferPointer + lengthOffset) = checked((uint)nameBytes);
            Buffer.MemoryCopy(
                namePointer,
                bufferPointer + nameOffset,
                nameBytes,
                nameBytes);
            if (NativeMethods.SetFileInformationByHandle(
                    source,
                    FileRenameInfoClass,
                    bufferPointer,
                    checked((uint)bufferSize)))
            {
                return FileMutationResult.Success();
            }

            return FailureFromWin32(Marshal.GetLastPInvokeError(), rootFailure: false);
        }
    }

    private static string CombineRootRelativePath(string root, string[] components)
    {
        string path = Path.TrimEndingDirectorySeparator(root);
        foreach (string component in components)
        {
            path = string.Concat(path, "\\", component);
        }

        return path;
    }

    private static unsafe FileMutationResult MarkForDeletion(SafeFileHandle handle)
    {
        FileDispositionInformation disposition = new() { DeleteFile = 1 };
        if (NativeMethods.SetFileInformationByHandle(
                handle,
                FileDispositionInfoClass,
                &disposition,
                (uint)sizeof(FileDispositionInformation)))
        {
            return FileMutationResult.Success();
        }

        int error = Marshal.GetLastPInvokeError();
        return error == ErrorDirectoryNotEmpty
            ? FileMutationResult.Failure(FileMutationFailureKind.DirectoryNotEmpty)
            : FailureFromWin32(error, rootFailure: false);
    }

    private static unsafe bool SetBasicInformation(
        SafeFileHandle destination,
        WindowsFileHandleSnapshot source)
    {
        FileBasicInformation basic = new()
        {
            CreationTime = source.CreationTime,
            LastAccessTime = source.LastAccessTime,
            LastWriteTime = source.LastWriteTime,
            ChangeTime = 0,
            FileAttributes = source.Attributes,
        };
        return NativeMethods.SetFileInformationByHandle(
            destination,
            FileBasicInfoClass,
            &basic,
            (uint)sizeof(FileBasicInformation));
    }

    private static FileMutationResult CleanupCreatedDestination(
        SafeFileHandle destination,
        FileMutationFailureKind originalFailure)
    {
        FileMutationResult cleanup = MarkForDeletion(destination);
        return cleanup.IsSuccess
            ? FileMutationResult.Failure(originalFailure)
            : FileMutationResult.Failure(
                FileMutationFailureKind.CleanupFailed,
                mutationMayHaveOccurred: true);
    }

    private static void CopyContents(
        SafeFileHandle source,
        SafeFileHandle destination,
        long length)
    {
        byte[] buffer = new byte[CopyBufferSize];
        long offset = 0;
        while (offset < length)
        {
            int requested = checked((int)Math.Min(buffer.Length, length - offset));
            int read = RandomAccess.Read(source, buffer.AsSpan(0, requested), offset);
            if (read <= 0)
            {
                throw new IOException("The locked source ended before its validated length.");
            }

            RandomAccess.Write(destination, buffer.AsSpan(0, read), offset);
            offset += read;
        }
    }

    private static string HashHandle(SafeFileHandle handle, long length)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[CopyBufferSize];
        long offset = 0;
        while (offset < length)
        {
            int requested = checked((int)Math.Min(buffer.Length, length - offset));
            int read = RandomAccess.Read(handle, buffer.AsSpan(0, requested), offset);
            if (read <= 0)
            {
                throw new IOException("The locked source ended before its validated length.");
            }

            hash.AppendData(buffer, 0, read);
            offset += read;
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static bool MatchesExpected(
        WindowsFileHandleSnapshot actual,
        FileMutationExpectedEntry expected)
    {
        if (actual.Kind != expected.Kind ||
            actual.IsReparsePoint ||
            !string.Equals(actual.VolumeIdentity, expected.VolumeIdentity, StringComparison.Ordinal) ||
            !string.Equals(actual.EntryIdentity, expected.EntryIdentity, StringComparison.Ordinal) ||
            (expected.Length is not null && actual.Length != expected.Length) ||
            (expected.CreationUtc is not null &&
             actual.CreationTime != expected.CreationUtc.Value.UtcDateTime.ToFileTimeUtc()) ||
            (expected.LastWriteUtc is not null &&
             actual.LastWriteTime != expected.LastWriteUtc.Value.UtcDateTime.ToFileTimeUtc()))
        {
            return false;
        }

        return expected.Attributes is null ||
            (int)((FileAttributes)actual.Attributes & RetainedAttributes) == expected.Attributes;
    }

    private static bool TryInspectExpectedKind(
        SafeFileHandle handle,
        FileSystemEntryKind expectedKind,
        string expectedVolumeIdentity,
        out WindowsFileHandleSnapshot? snapshot) =>
        WindowsFileHandleInspector.TryInspect(
            handle,
            includeCanonicalPath: false,
            out snapshot,
            out _) &&
        snapshot!.Kind == expectedKind &&
        !snapshot.IsReparsePoint &&
        string.Equals(
            snapshot.VolumeIdentity,
            expectedVolumeIdentity,
            StringComparison.Ordinal);

    private static FileMutationResult FailureFromWin32(int error, bool rootFailure) =>
        FileMutationResult.Failure(
            error switch
            {
                ErrorFileNotFound or ErrorPathNotFound => rootFailure
                    ? FileMutationFailureKind.RootChanged
                    : FileMutationFailureKind.SourceMissing,
                ErrorAccessDenied => FileMutationFailureKind.AccessDenied,
                ErrorFileExists or ErrorAlreadyExists =>
                    FileMutationFailureKind.DestinationExists,
                ErrorDirectoryNotEmpty => FileMutationFailureKind.DirectoryNotEmpty,
                ErrorSharingViolation or ErrorNotSameDevice =>
                    FileMutationFailureKind.PathChanged,
                ErrorInvalidName => FileMutationFailureKind.InvalidRequest,
                _ => FileMutationFailureKind.IoFailure,
            });

    private static FileMutationPreparationResult PreparationFailure(
        FileMutationResult result)
    {
        if (result.IsSuccess || result.MutationMayHaveOccurred)
        {
            throw new ArgumentException(
                "A preparation failure cannot report a completed or ambiguous mutation.",
                nameof(result));
        }

        return FileMutationPreparationResult.Failure(result.FailureKind);
    }

    private static bool TryParseRelativePath(
        string? relativePath,
        bool allowNull,
        out string[]? components)
    {
        components = null;
        if (relativePath is null)
        {
            return allowNull;
        }

        if (relativePath.Length is 0 or > 32_000 ||
            relativePath[0] == '\\' ||
            relativePath.Contains('/') ||
            relativePath.Contains(':') ||
            relativePath.Contains('\0'))
        {
            return false;
        }

        components = relativePath.Split('\\');
        return components.Length is > 0 and <= 128 &&
            components.All(static component =>
                component.Length is > 0 and <= 255 &&
                component is not ("." or ".."));
    }

    private static bool PhysicalPathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            StringComparison.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public uint Length;
        public nint RootDirectory;
        public nint ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor;
        public nint SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint Status;
        public nuint Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInformation
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        public byte DeleteFile;
    }

    private sealed class PreparedWindowsMutation(
        HandleLease handles,
        Func<FileMutationResult> effect) : IPreparedFileMutation
    {
        private bool executed;
        private bool disposed;

        public FileMutationResult Execute()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (executed)
            {
                throw new InvalidOperationException(
                    "A prepared file mutation can execute only once.");
            }

            executed = true;
            return effect();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            handles.Dispose();
        }
    }

    private sealed class HandleLease : IDisposable
    {
        private readonly List<SafeFileHandle> handles = [];
        private bool disposed;

        public void Add(SafeFileHandle handle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            handles.Add(handle);
        }

        public void DisposeHandle(SafeFileHandle handle)
        {
            if (handles.Remove(handle))
            {
                handle.Dispose();
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            for (int index = handles.Count - 1; index >= 0; index--)
            {
                handles[index].Dispose();
            }

            handles.Clear();
        }
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

        [DllImport("ntdll.dll")]
        internal static extern unsafe int NtCreateFile(
            out SafeFileHandle fileHandle,
            uint desiredAccess,
            ObjectAttributes* objectAttributes,
            IoStatusBlock* ioStatusBlock,
            long* allocationSize,
            uint fileAttributes,
            uint shareAccess,
            uint createDisposition,
            uint createOptions,
            void* eaBuffer,
            uint eaLength);

        [LibraryImport("ntdll.dll")]
        internal static partial uint RtlNtStatusToDosError(int status);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static unsafe partial bool SetFileInformationByHandle(
            SafeFileHandle file,
            int fileInformationClass,
            void* fileInformation,
            uint bufferSize);
    }
}

internal interface IWindowsFileMutationBoundaryHook
{
    void AfterHandlesLockedBeforeEffect(FileMutationRequest request);
}

internal sealed class NoWindowsFileMutationBoundaryHook : IWindowsFileMutationBoundaryHook
{
    public static NoWindowsFileMutationBoundaryHook Instance { get; } = new();

    private NoWindowsFileMutationBoundaryHook()
    {
    }

    public void AfterHandlesLockedBeforeEffect(FileMutationRequest request) =>
        ArgumentNullException.ThrowIfNull(request);
}
