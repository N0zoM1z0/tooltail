using Tooltail.Domain;

namespace Tooltail.Domain.Tests;

public sealed class AssemblyBoundaryTests
{
    [Fact]
    public void DomainAssemblyHasExpectedIdentity()
    {
        Assert.Equal("Tooltail.Domain", typeof(DomainAssembly).Assembly.GetName().Name);
    }
}
