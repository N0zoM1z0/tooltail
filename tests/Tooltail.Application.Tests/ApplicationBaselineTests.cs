using Microsoft.Extensions.DependencyInjection;
using Tooltail.Application;
using Tooltail.Application.Abstractions;
using Tooltail.Testing;

namespace Tooltail.Application.Tests;

public sealed class ApplicationBaselineTests
{
    [Fact]
    public void ApplicationRegistrationsUseReplaceableClockAndIdGenerator()
    {
        ServiceCollection services = new();

        services.AddTooltailApplication();

        Assert.Contains(services, static descriptor => descriptor.ServiceType == typeof(IClock));
        Assert.Contains(services, static descriptor => descriptor.ServiceType == typeof(IIdGenerator));
    }

    [Fact]
    public void FixedClockIsDeterministic()
    {
        DateTimeOffset expected = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        IClock clock = new FixedClock(expected);

        Assert.Equal(expected, clock.UtcNow);
        Assert.Equal(expected, clock.UtcNow);
    }

    [Fact]
    public void TemporaryDirectoryKeepsFixturesUnderAnIsolatedRoot()
    {
        using TemporaryDirectory directory = new();

        string file = directory.CreateTextFile("nested/example.txt", "synthetic");

        Assert.StartsWith(directory.Path, file, StringComparison.Ordinal);
        Assert.Equal("synthetic", File.ReadAllText(file));
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
