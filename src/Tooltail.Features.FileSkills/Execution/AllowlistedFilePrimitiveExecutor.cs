using Tooltail.Domain.Execution;

namespace Tooltail.Features.FileSkills.Execution;

/// <summary>
/// The sole learned-skill mutation surface. It maps the closed primitive vocabulary directly
/// to BCL file APIs and never invokes a shell, overwrites, deletes, or changes volumes.
/// </summary>
internal static class AllowlistedFilePrimitiveExecutor
{
    public static void Execute(
        PlannedFileOperation operation,
        PreparedExecutionPaths paths)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(paths);

        switch (operation.Primitive)
        {
            case FilePrimitive.EnsureDirectory:
                Directory.CreateDirectory(paths.Destination.FullPath);
                return;
            case FilePrimitive.RenameFile:
            case FilePrimitive.MoveFile:
                File.Move(
                    paths.Source!.FullPath,
                    paths.Destination.FullPath,
                    overwrite: false);
                return;
            case FilePrimitive.CopyFile:
                File.Copy(
                    paths.Source!.FullPath,
                    paths.Destination.FullPath,
                    overwrite: false);
                return;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(operation),
                    "Only the closed v0.1 file primitives can execute.");
        }
    }
}
