using System.Security;
using System.Security.Cryptography;
using Tooltail.Application.Abstractions;
using Tooltail.Domain.Execution;
using Tooltail.Features.FileSkills.Paths;

namespace Tooltail.Features.FileSkills.Undo;

internal sealed record PreparedRecoveryPaths(
    BoundLocalPath Source,
    BoundLocalPath? Destination);

internal sealed record RecoveryPreconditionResult(
    bool IsSuccess,
    string ReasonCode,
    PreparedRecoveryPaths? Paths)
{
    public static RecoveryPreconditionResult Success(PreparedRecoveryPaths paths) =>
        new(true, "undo.preconditions_satisfied", paths);

    public static RecoveryPreconditionResult Failure(string reasonCode) =>
        new(false, reasonCode, null);
}

internal sealed class RecoveryPathPreconditionValidator
{
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

    private readonly WindowsPathSafetyService pathSafety;
    private readonly long maximumFileBytes;

    public RecoveryPathPreconditionValidator(
        WindowsPathSafetyService pathSafety,
        long maximumFileBytes)
    {
        ArgumentNullException.ThrowIfNull(pathSafety);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFileBytes, 1);
        this.pathSafety = pathSafety;
        this.maximumFileBytes = maximumFileBytes;
    }

    public async ValueTask<RecoveryPreconditionResult> PrepareAsync(
        CanonicalLocalRoot root,
        PlannedRecoveryOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(operation);
        PathSafetyResult<BoundLocalPath> source = pathSafety.Bind(
            root,
            operation.SourceRelativePath,
            PathEntryExpectation.MustExist);
        if (!source.IsSuccess)
        {
            return RecoveryPreconditionResult.Failure(source.Error!.Code);
        }

        if (!ParentsAreExistingDirectories(source.Value!))
        {
            return RecoveryPreconditionResult.Failure("undo.source_parent_invalid");
        }

        string? sourceFailure = await ValidateSourceAsync(
            root,
            source.Value!,
            operation,
            cancellationToken).ConfigureAwait(false);
        if (sourceFailure is not null)
        {
            return RecoveryPreconditionResult.Failure(sourceFailure);
        }

        BoundLocalPath? destinationPath = null;
        if (operation.DestinationRelativePath is not null)
        {
            PathSafetyResult<BoundLocalPath> destination = pathSafety.Bind(
                root,
                operation.DestinationRelativePath,
                PathEntryExpectation.MustNotExist);
            if (!destination.IsSuccess)
            {
                return RecoveryPreconditionResult.Failure(destination.Error!.Code);
            }

            if (!ParentsAreExistingDirectories(destination.Value!))
            {
                return RecoveryPreconditionResult.Failure("undo.destination_parent_invalid");
            }

            destinationPath = destination.Value!;
        }

        return RecoveryPreconditionResult.Success(
            new PreparedRecoveryPaths(source.Value!, destinationPath));
    }

    public async ValueTask<RecoveryPreconditionResult> RevalidateAsync(
        CanonicalLocalRoot root,
        PreparedRecoveryPaths paths,
        PlannedRecoveryOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(operation);
        PathSafetyResult<BoundLocalPath> source = pathSafety.Revalidate(paths.Source);
        if (!source.IsSuccess || !ParentsAreExistingDirectories(source.Value!))
        {
            return RecoveryPreconditionResult.Failure(
                source.Error?.Code ?? "undo.source_parent_invalid");
        }

        string? sourceFailure = await ValidateSourceAsync(
            root,
            source.Value!,
            operation,
            cancellationToken).ConfigureAwait(false);
        if (sourceFailure is not null)
        {
            return RecoveryPreconditionResult.Failure(sourceFailure);
        }

        BoundLocalPath? destinationPath = null;
        if (paths.Destination is not null)
        {
            PathSafetyResult<BoundLocalPath> destination =
                pathSafety.Revalidate(paths.Destination);
            if (!destination.IsSuccess ||
                !ParentsAreExistingDirectories(destination.Value!))
            {
                return RecoveryPreconditionResult.Failure(
                    destination.Error?.Code ?? "undo.destination_parent_invalid");
            }

            destinationPath = destination.Value!;
        }

        PathSafetyResult<BoundLocalPath> sourceAfterInspection =
            pathSafety.Revalidate(source.Value!);
        if (!sourceAfterInspection.IsSuccess)
        {
            return RecoveryPreconditionResult.Failure(sourceAfterInspection.Error!.Code);
        }

        BoundLocalPath? destinationAfterInspection = null;
        if (destinationPath is not null)
        {
            PathSafetyResult<BoundLocalPath> destination =
                pathSafety.Revalidate(destinationPath);
            if (!destination.IsSuccess)
            {
                return RecoveryPreconditionResult.Failure(
                    destination.Error!.Code);
            }

            destinationAfterInspection = destination.Value!;
        }

        return RecoveryPreconditionResult.Success(
            new PreparedRecoveryPaths(
                sourceAfterInspection.Value!,
                destinationAfterInspection));
    }

    private async ValueTask<string?> ValidateSourceAsync(
        CanonicalLocalRoot root,
        BoundLocalPath source,
        PlannedRecoveryOperation operation,
        CancellationToken cancellationToken)
    {
        VerifiedEntryEvidence expected = operation.ExpectedSource;
        PathComponentBinding final = source.Components[^1];
        FileSystemEntryKind requiredKind = expected.Kind == VerifiedEntryKind.File
            ? FileSystemEntryKind.File
            : FileSystemEntryKind.Directory;
        if (final.EntryKind != requiredKind ||
            !string.Equals(root.VolumeIdentity, expected.VolumeIdentity, StringComparison.Ordinal) ||
            !string.Equals(final.EntryIdentity, expected.EntryIdentity, StringComparison.Ordinal))
        {
            return "undo.source_identity_changed";
        }

        try
        {
            if (expected.Kind == VerifiedEntryKind.Directory)
            {
                DirectoryInfo directory = new(source.FullPath);
                directory.Refresh();
                if (!directory.Exists ||
                    (int)(directory.Attributes & RetainedAttributes) != expected.Attributes)
                {
                    return "undo.source_fingerprint_changed";
                }

                if (operation.Primitive == RecoveryPrimitive.RemoveCreatedEntry &&
                    directory.EnumerateFileSystemInfos(
                        "*",
                        new EnumerationOptions
                        {
                            AttributesToSkip = 0,
                            IgnoreInaccessible = false,
                            RecurseSubdirectories = false,
                            ReturnSpecialDirectories = false,
                        }).Any())
                {
                    return "undo.created_directory_not_empty";
                }

                return null;
            }

            if (expected.Length is null ||
                expected.ContentHash is null ||
                expected.Length > maximumFileBytes)
            {
                return expected.Length > maximumFileBytes
                    ? "undo.source_file_limit_exceeded"
                    : "undo.source_hash_required";
            }

            FileInfo file = new(source.FullPath);
            file.Refresh();
            if (!file.Exists ||
                file.Length != expected.Length ||
                AsUtc(file.CreationTimeUtc) != expected.CreationUtc ||
                AsUtc(file.LastWriteTimeUtc) != expected.LastWriteUtc ||
                (int)(file.Attributes & RetainedAttributes) != expected.Attributes)
            {
                return "undo.source_fingerprint_changed";
            }

            await using FileStream stream = new(
                source.FullPath,
                new FileStreamOptions
                {
                    Access = FileAccess.Read,
                    BufferSize = 64 * 1024,
                    Mode = FileMode.Open,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                    Share = FileShare.Read,
                });
            byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    Convert.ToHexStringLower(digest),
                    expected.ContentHash.Value,
                    StringComparison.Ordinal))
            {
                return "undo.source_hash_changed";
            }

            file.Refresh();
            return !file.Exists ||
                file.Length != expected.Length ||
                AsUtc(file.CreationTimeUtc) != expected.CreationUtc ||
                AsUtc(file.LastWriteTimeUtc) != expected.LastWriteUtc ||
                (int)(file.Attributes & RetainedAttributes) != expected.Attributes
                ? "undo.source_changed_during_hash"
                : null;
        }
        catch (OperationCanceledException)
        {
            return "undo.cancelled";
        }
        catch (UnauthorizedAccessException)
        {
            return "undo.source_access_denied";
        }
        catch (SecurityException)
        {
            return "undo.source_access_denied";
        }
        catch (IOException)
        {
            return "undo.source_inspection_failed";
        }
    }

    private static bool ParentsAreExistingDirectories(BoundLocalPath path) =>
        path.Components
            .Take(path.Components.Count - 1)
            .All(static component =>
                component.Existed &&
                component.EntryKind == FileSystemEntryKind.Directory);

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
