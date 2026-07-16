using Tooltail.Adapters.AgentEvents.Simulator;

namespace Tooltail.Adapters.AgentEvents.Tests.Simulator;

public sealed class SimulatorTraceCatalogTests
{
    private static readonly string[] RequiredTraceNames =
    [
        "normal-start-tool-complete",
        "observation-only",
        "input-request-resolution",
        "parallel-two-units",
        "tool-and-run-failure",
        "pause-resume-cancel",
        "permission-revoked-mid-tool",
        "adapter-disconnect",
        "blocked-and-resumed",
        "duplicate-event",
        "malformed-jsonl",
        "delayed-time-regression",
        "out-of-order-sequence",
        "oversized-line",
        "event-stream-limit",
    ];

    [Fact]
    public void CatalogContainsEveryRequiredTraceInStableOrder()
    {
        Assert.Equal(
            RequiredTraceNames,
            SimulatorTraceCatalog.All.Select(static trace => trace.Name));
        Assert.Equal(
            RequiredTraceNames.Length,
            SimulatorTraceCatalog.All
                .Select(static trace => trace.Name)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public async Task EveryTraceMatchesItsBoundedExpectation()
    {
        foreach (SimulatorTraceDefinition trace in SimulatorTraceCatalog.All)
        {
            SimulatorTraceVerification verification =
                await SimulatorTraceEvaluator.VerifyAsync(trace);

            Assert.True(
                verification.IsMatch,
                $"{trace.Name}: {string.Join(", ", verification.MismatchCodes)}; " +
                $"actual={verification.Actual.Status}/{verification.Actual.ReasonCode}");
        }
    }

    [Fact]
    public async Task FullStateSequenceMatchesExactCommittedGolden()
    {
        SimulatorTraceCatalogReport report =
            await SimulatorTraceEvaluator.CreateCatalogReportAsync();
        string actual = SimulatorTraceGoldenFormatter.Format(report);
        string expected = await File.ReadAllTextAsync(
            Path.Combine(
                FindRepositoryRoot(),
                "tests",
                "Tooltail.Adapters.AgentEvents.Tests",
                "Golden",
                "simulator-state-sequences.golden.txt"));

        Assert.True(report.AllMatched);
        Assert.Equal(NormalizeNewlines(expected), NormalizeNewlines(actual));
    }

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tooltail.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate Tooltail.sln from the adapter test output directory.");
    }
}
