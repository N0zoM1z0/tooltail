using Tooltail.Infrastructure.Sqlite;

namespace Tooltail.Infrastructure.Sqlite.Tests;

public sealed class AssemblyBoundaryTests
{
    [Fact]
    public void SqliteAssemblyHasExpectedIdentity()
    {
        Assert.Equal("Tooltail.Infrastructure.Sqlite", typeof(SqliteAssembly).Assembly.GetName().Name);
    }
}
