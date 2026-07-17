using Tooltail.Application.Abstractions;
using Tooltail.Domain.Execution;

namespace Tooltail.Features.FileSkills.Undo;

/// <summary>
/// The sole internal recovery mutation surface. It is unreachable from SkillSpec/compiler
/// output and accepts only identity-bound paths prepared from durable execution evidence.
/// </summary>
internal static class AllowlistedRecoveryPrimitiveExecutor
{
    public static FileMutationPreparationResult Prepare(
        IFileMutationEngine mutationEngine,
        PlannedRecoveryOperation operation,
        PreparedRecoveryPaths paths)
    {
        ArgumentNullException.ThrowIfNull(mutationEngine);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(paths);

        FileMutationRootBinding root = new(
            paths.Source.Root.CanonicalPath,
            paths.Source.Root.VolumeIdentity,
            paths.Source.Root.EntryIdentity);
        FileMutationExpectedEntry expected = ExpectedSource(operation.ExpectedSource);
        return operation.Primitive switch
        {
            RecoveryPrimitive.RenameBack or RecoveryPrimitive.MoveBack =>
                mutationEngine.Prepare(
                    FileMutationRequest.MoveFile(
                        root,
                        operation.SourceRelativePath,
                        operation.DestinationRelativePath!,
                        expected)),
            RecoveryPrimitive.RemoveCreatedEntry =>
                mutationEngine.Prepare(
                    FileMutationRequest.RemoveCreatedEntry(
                        root,
                        operation.SourceRelativePath,
                        expected)),
            _ => FileMutationPreparationResult.Failure(FileMutationFailureKind.InvalidRequest),
        };
    }

    private static FileMutationExpectedEntry ExpectedSource(
        VerifiedEntryEvidence source) =>
        new(
            source.Kind == VerifiedEntryKind.File
                ? FileSystemEntryKind.File
                : FileSystemEntryKind.Directory,
            source.VolumeIdentity,
            source.EntryIdentity,
            source.Length,
            source.Kind == VerifiedEntryKind.File ? source.CreationUtc : null,
            source.Kind == VerifiedEntryKind.File ? source.LastWriteUtc : null,
            source.Attributes,
            source.ContentHash?.Value);
}
