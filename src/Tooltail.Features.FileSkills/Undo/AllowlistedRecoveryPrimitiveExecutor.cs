using Tooltail.Domain.Execution;

namespace Tooltail.Features.FileSkills.Undo;

/// <summary>
/// The sole internal recovery mutation surface. It is unreachable from SkillSpec/compiler
/// output and accepts only identity-bound paths prepared from durable execution evidence.
/// </summary>
internal static class AllowlistedRecoveryPrimitiveExecutor
{
    public static void Execute(
        PlannedRecoveryOperation operation,
        PreparedRecoveryPaths paths)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(paths);
        switch (operation.Primitive)
        {
            case RecoveryPrimitive.RenameBack:
            case RecoveryPrimitive.MoveBack:
                File.Move(
                    paths.Source.FullPath,
                    paths.Destination!.FullPath,
                    overwrite: false);
                return;
            case RecoveryPrimitive.RemoveCreatedEntry
                when operation.ExpectedSource.Kind == VerifiedEntryKind.File:
                File.Delete(paths.Source.FullPath);
                return;
            case RecoveryPrimitive.RemoveCreatedEntry
                when operation.ExpectedSource.Kind == VerifiedEntryKind.Directory:
                Directory.Delete(paths.Source.FullPath, recursive: false);
                return;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(operation),
                    "Only the closed internal recovery primitives can execute.");
        }
    }
}
