using System.Security;
using System.Security.Cryptography;
using Tooltail.Domain.Execution;
using Tooltail.Features.FileSkills.Paths;

namespace Tooltail.Features.FileSkills.Execution;

internal sealed record PreparedExecutionPaths(
    BoundLocalPath? Source,
    BoundLocalPath Destination);

internal sealed record ExecutionPreconditionResult(
    bool IsSuccess,
    string ReasonCode,
    PreparedExecutionPaths? Paths)
{
    public static ExecutionPreconditionResult Success(PreparedExecutionPaths paths) =>
        new(true, "execution.preconditions_satisfied", paths);

    public static ExecutionPreconditionResult Failure(string reasonCode) =>
        new(false, reasonCode, null);
}

internal sealed class ExecutionPathPreconditionValidator
{
    private readonly WindowsPathSafetyService pathSafety;
    private readonly FileExecutionLimits limits;

    public ExecutionPathPreconditionValidator(
        WindowsPathSafetyService pathSafety,
        FileExecutionLimits limits)
    {
        ArgumentNullException.ThrowIfNull(pathSafety);
        ArgumentNullException.ThrowIfNull(limits);
        this.pathSafety = pathSafety;
        this.limits = limits;
    }

    public async ValueTask<ExecutionPreconditionResult> PrepareAsync(
        CanonicalLocalRoot root,
        PlannedFileOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(operation);

        PathEntryExpectation destinationExpectation = operation.DestinationPrecondition ==
            DestinationPrecondition.Absent
            ? PathEntryExpectation.MustNotExist
            : PathEntryExpectation.MustExist;
        PathSafetyResult<BoundLocalPath> destination = pathSafety.Bind(
            root,
            operation.DestinationRelativePath,
            destinationExpectation);
        if (!destination.IsSuccess)
        {
            return ExecutionPreconditionResult.Failure(destination.Error!.Code);
        }

        if (!ParentsAreExistingDirectories(destination.Value!))
        {
            return ExecutionPreconditionResult.Failure("execution.destination_parent_missing");
        }

        if (operation.Primitive == FilePrimitive.EnsureDirectory)
        {
            if (operation.DestinationPrecondition == DestinationPrecondition.ExistingDirectory &&
                destination.Value!.Components[^1].EntryKind !=
                Tooltail.Application.Abstractions.FileSystemEntryKind.Directory)
            {
                return ExecutionPreconditionResult.Failure("execution.destination_not_directory");
            }

            return ExecutionPreconditionResult.Success(
                new PreparedExecutionPaths(null, destination.Value!));
        }

        PathSafetyResult<BoundLocalPath> source = pathSafety.Bind(
            root,
            operation.SourceRelativePath,
            PathEntryExpectation.MustExist);
        if (!source.IsSuccess)
        {
            return ExecutionPreconditionResult.Failure(source.Error!.Code);
        }

        if (!ParentsAreExistingDirectories(source.Value!) ||
            source.Value!.Components[^1].EntryKind !=
            Tooltail.Application.Abstractions.FileSystemEntryKind.File)
        {
            return ExecutionPreconditionResult.Failure("execution.source_not_regular_file");
        }

        string? sourceFailure = await ValidateSourceFingerprintAsync(
            source.Value,
            operation.SourceFingerprint!,
            cancellationToken).ConfigureAwait(false);
        return sourceFailure is null
            ? ExecutionPreconditionResult.Success(
                new PreparedExecutionPaths(source.Value, destination.Value!))
            : ExecutionPreconditionResult.Failure(sourceFailure);
    }

    public async ValueTask<ExecutionPreconditionResult> RevalidateAsync(
        PreparedExecutionPaths paths,
        PlannedFileOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(operation);

        PathSafetyResult<BoundLocalPath> destination = pathSafety.Revalidate(paths.Destination);
        if (!destination.IsSuccess)
        {
            return ExecutionPreconditionResult.Failure(destination.Error!.Code);
        }

        if (!ParentsAreExistingDirectories(destination.Value!))
        {
            return ExecutionPreconditionResult.Failure("execution.destination_parent_missing");
        }

        if (paths.Source is null)
        {
            return ExecutionPreconditionResult.Success(
                new PreparedExecutionPaths(null, destination.Value!));
        }

        PathSafetyResult<BoundLocalPath> source = pathSafety.Revalidate(paths.Source);
        if (!source.IsSuccess)
        {
            return ExecutionPreconditionResult.Failure(source.Error!.Code);
        }

        if (!ParentsAreExistingDirectories(source.Value!) ||
            source.Value!.Components[^1].EntryKind !=
            Tooltail.Application.Abstractions.FileSystemEntryKind.File)
        {
            return ExecutionPreconditionResult.Failure("execution.source_not_regular_file");
        }

        string? sourceFailure = await ValidateSourceFingerprintAsync(
            source.Value,
            operation.SourceFingerprint!,
            cancellationToken).ConfigureAwait(false);
        if (sourceFailure is not null)
        {
            return ExecutionPreconditionResult.Failure(sourceFailure);
        }

        PathSafetyResult<BoundLocalPath> sourceAfterInspection = pathSafety.Revalidate(source.Value);
        PathSafetyResult<BoundLocalPath> destinationAfterInspection = pathSafety.Revalidate(destination.Value!);
        if (!sourceAfterInspection.IsSuccess)
        {
            return ExecutionPreconditionResult.Failure(sourceAfterInspection.Error!.Code);
        }

        if (!destinationAfterInspection.IsSuccess)
        {
            return ExecutionPreconditionResult.Failure(destinationAfterInspection.Error!.Code);
        }

        return ExecutionPreconditionResult.Success(
            new PreparedExecutionPaths(
                sourceAfterInspection.Value,
                destinationAfterInspection.Value!));
    }

    private async ValueTask<string?> ValidateSourceFingerprintAsync(
        BoundLocalPath source,
        SourceFileFingerprint expected,
        CancellationToken cancellationToken)
    {
        PathComponentBinding final = source.Components[^1];
        if (!string.Equals(final.EntryIdentity, expected.EntryIdentity, StringComparison.Ordinal))
        {
            return "execution.source_identity_changed";
        }

        if (expected.Length > limits.MaximumSourceFileBytes)
        {
            return "execution.source_file_limit_exceeded";
        }

        try
        {
            FileInfo file = new(source.FullPath);
            file.Refresh();
            if (!file.Exists ||
                file.Length != expected.Length ||
                AsUtc(file.LastWriteTimeUtc) != expected.LastWriteUtc)
            {
                return "execution.source_fingerprint_changed";
            }

            if (expected.ContentHash is not null)
            {
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
                    return "execution.source_hash_changed";
                }

                file.Refresh();
                if (!file.Exists ||
                    file.Length != expected.Length ||
                    AsUtc(file.LastWriteTimeUtc) != expected.LastWriteUtc)
                {
                    return "execution.source_changed_during_hash";
                }
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return "execution.cancelled";
        }
        catch (UnauthorizedAccessException)
        {
            return "execution.source_access_denied";
        }
        catch (SecurityException)
        {
            return "execution.source_access_denied";
        }
        catch (IOException)
        {
            return "execution.source_inspection_failed";
        }
    }

    private static bool ParentsAreExistingDirectories(BoundLocalPath path) =>
        path.Components
            .Take(path.Components.Count - 1)
            .All(static component =>
                component.Existed &&
                component.EntryKind ==
                Tooltail.Application.Abstractions.FileSystemEntryKind.Directory);

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
