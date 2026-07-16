using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tooltail.Application.Abstractions;
using Tooltail.Application.FileSkills;

namespace Tooltail.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTooltailApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IIdGenerator, GuidIdGenerator>();
        services.TryAddSingleton<FileApprenticeStartupService>();

        return services;
    }
}
