using Tooltail.Platform.Windows;

namespace Tooltail.Platform.Windows.Tests;

public sealed class AssemblyBoundaryTests
{
    [Fact]
    public void WindowsPlatformAssemblyHasExpectedIdentity()
    {
        Assert.Equal("Tooltail.Platform.Windows", typeof(WindowsPlatformAssembly).Assembly.GetName().Name);
    }
}
