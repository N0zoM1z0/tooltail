using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tooltail.Application;
using Tooltail.Application.Abstractions;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(
    new HostApplicationBuilderSettings
    {
        ApplicationName = "Tooltail.SkillFixtureCli",
        Args = [],
    });

builder.Logging.ClearProviders();
builder.Services.AddTooltailApplication();

using IHost host = builder.Build();
_ = host.Services.GetRequiredService<IClock>();

return Run(args);

static int Run(string[] arguments)
{
    string command = arguments.Length == 0 ? "help" : arguments[0].ToLowerInvariant();

    switch (command)
    {
        case "help":
        case "--help":
        case "-h":
            WriteHelp();
            return 0;
        case "version":
        case "--version":
            Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0");
            return 0;
        default:
            Console.Error.WriteLine($"Unknown command '{command}'. Run with --help for supported commands.");
            return 2;
    }
}

static void WriteHelp()
{
    Console.WriteLine("Tooltail Skill Fixture CLI");
    Console.WriteLine();
    Console.WriteLine("Usage: tooltail-skill-fixture <command>");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  help       Show this help without accessing user data.");
    Console.WriteLine("  version    Print the tool assembly version.");
    Console.WriteLine();
    Console.WriteLine("File-skill fixture commands are introduced in milestone M2.");
}
