using Tooltail.Domain.Execution;

namespace Tooltail.Domain.Tests;

public sealed class ExecutionPlanSafetyTests
{
    [Fact]
    public void EnsureDirectoryRequiresExactAbsentDestination()
    {
        Assert.Throws<ArgumentException>(
            () => new PlannedFileOperation(
                sequence: 1,
                FilePrimitive.EnsureDirectory,
                sourceRelativePath: null,
                destinationRelativePath: "Review",
                sourceFingerprint: null,
                DestinationPrecondition.ExistingDirectory,
                ExpectedSourceState.NotApplicable,
                ExpectedDestinationState.DirectoryPresent));
    }
}
