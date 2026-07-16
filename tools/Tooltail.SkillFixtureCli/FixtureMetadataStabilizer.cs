using Tooltail.Domain.Execution;
using Tooltail.Features.FileSkills.Execution;
using Tooltail.Features.FileSkills.Paths;

namespace Tooltail.SkillFixtureCli;

/// <summary>
/// Normalizes metadata created by fixture-only primitives so exact receipt and recovery
/// goldens do not depend on wall-clock filesystem timestamps. Production composition never
/// installs this test boundary.
/// </summary>
internal sealed class FixtureMetadataStabilizer : IFileExecutionFaultInjector
{
    private readonly DateTime fixedDirectoryUtc;
    private readonly Dictionary<int, PlannedFileOperation> operations;
    private readonly CanonicalLocalRoot root;

    public FixtureMetadataStabilizer(
        CanonicalLocalRoot root,
        ExecutionPlan plan,
        DateTimeOffset fixedDirectoryUtc)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(plan);
        if (fixedDirectoryUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Fixture metadata time must use UTC.", nameof(fixedDirectoryUtc));
        }

        this.root = root;
        this.fixedDirectoryUtc = fixedDirectoryUtc.UtcDateTime;
        operations = plan.Definition.Operations.ToDictionary(static operation => operation.Sequence);
    }

    public void Reach(FileExecutionBoundaryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Boundary != FileExecutionBoundary.AfterPrimitive ||
            context.StepSequence is null ||
            !operations.TryGetValue(context.StepSequence.Value, out PlannedFileOperation? operation))
        {
            return;
        }

        string destination = Resolve(operation.DestinationRelativePath);
        if (operation.Primitive == FilePrimitive.EnsureDirectory)
        {
            Directory.SetCreationTimeUtc(destination, fixedDirectoryUtc);
            Directory.SetLastWriteTimeUtc(destination, fixedDirectoryUtc);
            return;
        }

        if (operation.Primitive == FilePrimitive.CopyFile)
        {
            string source = Resolve(operation.SourceRelativePath!);
            File.SetAttributes(destination, File.GetAttributes(source));
            File.SetCreationTimeUtc(destination, File.GetCreationTimeUtc(source));
            File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
        }
    }

    private string Resolve(string relativePath)
    {
        PathSafetyResult<WindowsRelativePath> parsed = WindowsPathPolicy.ParseRelative(relativePath);
        if (!parsed.IsSuccess)
        {
            throw new InvalidOperationException(parsed.Error!.Code);
        }

        return parsed.Value!.Value
            .Split('\\')
            .Aggregate(root.CanonicalPath, Path.Combine);
    }
}
