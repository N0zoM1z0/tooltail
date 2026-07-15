using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tooltail.Application.Abstractions;

namespace Tooltail.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTooltailApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IIdGenerator, GuidIdGenerator>();

        return services;
    }
}
