using Microsoft.Extensions.DependencyInjection;

namespace UptimeHarbor.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(TimeProvider.System);

        return services;
    }
}
