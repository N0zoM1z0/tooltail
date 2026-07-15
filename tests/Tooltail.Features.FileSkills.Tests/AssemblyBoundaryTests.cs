using Tooltail.Features.FileSkills;

namespace Tooltail.Features.FileSkills.Tests;

public sealed class AssemblyBoundaryTests
{
    [Fact]
    public void FileSkillsAssemblyHasExpectedIdentity()
    {
        Assert.Equal("Tooltail.Features.FileSkills", typeof(FileSkillsAssembly).Assembly.GetName().Name);
    }
}
