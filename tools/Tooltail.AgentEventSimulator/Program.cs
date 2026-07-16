using Tooltail.Adapters.AgentEvents.Simulator;
using Tooltail.Contracts.Json;

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    Console.WriteLine("Tooltail Agent Event Simulator");
    Console.WriteLine("  list");
    Console.WriteLine("  emit --trace <name>");
    Console.WriteLine("  project --trace <name>");
    Console.WriteLine("  verify-all");
    Console.WriteLine("  golden");
    return 0;
}

if (args is ["list"])
{
    var report = new SimulatorListReport(
        ContractVersions.V1,
        SimulatorTraceCatalog.All
            .Select(static trace => new SimulatorListItem(
                trace.Name,
                trace.Description,
                trace.JsonlByteCount,
                trace.Expectation.Status,
                trace.Expectation.ReasonCode))
            .ToArray());
    await WriteJsonAsync(report);
    return 0;
}

if (args is ["emit", "--trace", string emitName])
{
    if (!SimulatorTraceCatalog.TryGet(emitName, out SimulatorTraceDefinition? trace))
    {
        Console.Error.WriteLine($"Unknown trace '{emitName}'. Run 'list' for names.");
        return 2;
    }

    await Console.OpenStandardOutput().WriteAsync(trace.ExportJsonl());
    return 0;
}

if (args is ["project", "--trace", string projectName])
{
    if (!SimulatorTraceCatalog.TryGet(projectName, out SimulatorTraceDefinition? trace))
    {
        Console.Error.WriteLine($"Unknown trace '{projectName}'. Run 'list' for names.");
        return 2;
    }

    SimulatorTraceVerification verification = await SimulatorTraceEvaluator.VerifyAsync(trace);
    await WriteJsonAsync(SimulatorTraceEvaluator.ToReport(verification));
    return verification.IsMatch ? 0 : 1;
}

if (args is ["verify-all"])
{
    SimulatorTraceCatalogReport report =
        await SimulatorTraceEvaluator.CreateCatalogReportAsync();
    await WriteJsonAsync(report);
    return report.AllMatched ? 0 : 1;
}

if (args is ["golden"])
{
    SimulatorTraceCatalogReport report =
        await SimulatorTraceEvaluator.CreateCatalogReportAsync();
    byte[] golden = System.Text.Encoding.UTF8.GetBytes(
        SimulatorTraceGoldenFormatter.Format(report));
    await Console.OpenStandardOutput().WriteAsync(golden);
    return report.AllMatched ? 0 : 1;
}

Console.Error.WriteLine($"Unknown command '{args[0]}'. Run with --help for supported commands.");
return 2;

static async Task WriteJsonAsync<T>(T value)
{
    byte[] json = ContractJson.Serialize(value);
    Stream output = Console.OpenStandardOutput();
    await output.WriteAsync(json);
    await output.WriteAsync("\n"u8.ToArray());
}

internal sealed record SimulatorListReport(
    string SchemaVersion,
    IReadOnlyList<SimulatorListItem> Traces);

internal sealed record SimulatorListItem(
    string Name,
    string Description,
    int JsonlByteCount,
    Tooltail.Adapters.AgentEvents.Streaming.AgentEventStreamStatus ExpectedStatus,
    string ExpectedReasonCode);
