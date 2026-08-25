using Microsoft.Extensions.DependencyInjection;
using UptimeHarbor.Application.Users;
using UptimeHarbor.Application.Visibility;

namespace UptimeHarbor.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IUserManagementService, UserManagementService>();
        services.AddScoped<IAccessibleObjectsService, AccessibleObjectsService>();

        return services;
    }
}
