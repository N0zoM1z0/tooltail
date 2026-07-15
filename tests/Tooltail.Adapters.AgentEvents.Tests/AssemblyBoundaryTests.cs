using Tooltail.Adapters.AgentEvents;

namespace Tooltail.Adapters.AgentEvents.Tests;

public sealed class AssemblyBoundaryTests
{
    [Fact]
    public void AgentEventsAssemblyHasExpectedIdentity()
    {
        Assert.Equal("Tooltail.Adapters.AgentEvents", typeof(AgentEventsAssembly).Assembly.GetName().Name);
    }
}
