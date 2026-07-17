using Tooltail.Application.Abstractions;
using Tooltail.Domain.Execution;

namespace Tooltail.Features.FileSkills.Execution;

/// <summary>
/// The sole learned-skill mutation surface. It maps the closed primitive vocabulary directly
/// to the injected closed mutation engine and never invokes a shell, overwrites, deletes,
/// or changes volumes.
/// </summary>
internal static class AllowlistedFilePrimitiveExecutor
{
    public static FileMutationPreparationResult Prepare(
        IFileMutationEngine mutationEngine,
        PlannedFileOperation operation,
        PreparedExecutionPaths paths,
        long maximumCopyBytes)
    {
        ArgumentNullException.ThrowIfNull(mutationEngine);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(paths);

        FileMutationRootBinding root = new(
            paths.Destination.Root.CanonicalPath,
            paths.Destination.Root.VolumeIdentity,
            paths.Destination.Root.EntryIdentity);

        return operation.Primitive switch
        {
            FilePrimitive.EnsureDirectory when
                operation.DestinationPrecondition == DestinationPrecondition.Absent =>
                mutationEngine.Prepare(
                    FileMutationRequest.CreateDirectory(
                        root,
                        operation.DestinationRelativePath)),
            FilePrimitive.RenameFile or FilePrimitive.MoveFile =>
                mutationEngine.Prepare(
                    FileMutationRequest.MoveFile(
                        root,
                        operation.SourceRelativePath!,
                        operation.DestinationRelativePath,
                        ExpectedSource(root, operation.SourceFingerprint!))),
            FilePrimitive.CopyFile =>
                mutationEngine.Prepare(
                    FileMutationRequest.CopyFile(
                        root,
                        operation.SourceRelativePath!,
                        operation.DestinationRelativePath,
                        ExpectedSource(root, operation.SourceFingerprint!),
                        maximumCopyBytes)),
            _ => FileMutationPreparationResult.Failure(FileMutationFailureKind.InvalidRequest),
        };
    }

    private static FileMutationExpectedEntry ExpectedSource(
        FileMutationRootBinding root,
        SourceFileFingerprint source) =>
        new(
            FileSystemEntryKind.File,
            root.VolumeIdentity,
            source.EntryIdentity,
            source.Length,
            lastWriteUtc: source.LastWriteUtc,
            contentHash: source.ContentHash?.Value);
}
