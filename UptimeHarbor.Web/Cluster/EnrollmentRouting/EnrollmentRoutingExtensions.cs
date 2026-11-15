using Microsoft.Extensions.DependencyInjection.Extensions;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;

namespace UptimeHarbor.Web.Cluster.EnrollmentRouting;

public static class EnrollmentRoutingExtensions
{
    public static IServiceCollection AddEnrollmentPeerRouting(this IServiceCollection services)
    {
        // Самостоятельный cluster module без local signing persistence сохраняет закрытый граф.
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(EnrollmentSigner)))
        {
            return services;
        }

        services.TryAddScoped<OnlineGrantSigner>();
        services.TryAddSingleton<EnrollmentAuthority>();
        services.Replace(ServiceDescriptor.Singleton<IEnrollmentAuthority>(provider => provider.GetRequiredService<EnrollmentAuthority>()));
        services.AddScoped<EnrollmentCommandReceiver>();
        services.AddScoped<IEnrollmentPeerClient, EnrollmentPeerClient>();
        services.AddScoped<EnrollmentPeerService>();
        services.AddScoped<EnrollmentIssuanceRouter>();
        return services;
    }
}
