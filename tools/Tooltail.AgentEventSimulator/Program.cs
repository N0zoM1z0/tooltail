using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tooltail.Application;
using Tooltail.Application.Abstractions;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(
    new HostApplicationBuilderSettings
    {
        ApplicationName = "Tooltail.AgentEventSimulator",
        Args = [],
    });

builder.Logging.ClearProviders();
builder.Services.AddTooltailApplication();

using IHost host = builder.Build();
_ = host.Services.GetRequiredService<IIdGenerator>();

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    Console.WriteLine("Tooltail Agent Event Simulator");
    Console.WriteLine("Deterministic trace playback is introduced in milestone M3.");
    return 0;
}

Console.Error.WriteLine($"Unknown command '{args[0]}'. Run with --help for supported commands.");
return 2;
