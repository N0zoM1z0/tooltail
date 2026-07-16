using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tooltail.Application;
using Tooltail.Application.Abstractions;
using Tooltail.SkillFixtureCli;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(
    new HostApplicationBuilderSettings
    {
        ApplicationName = "Tooltail.SkillFixtureCli",
        Args = [],
    });

builder.Logging.ClearProviders();
builder.Services.AddTooltailApplication();

using IHost host = builder.Build();
IClock clock = host.Services.GetRequiredService<IClock>();
IIdGenerator idGenerator = host.Services.GetRequiredService<IIdGenerator>();

return await FixtureCliApplication.RunAsync(
    args,
    Console.Out,
    clock,
    idGenerator).ConfigureAwait(false);
