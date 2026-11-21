using Microsoft.Extensions.DependencyInjection.Extensions;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Installation.Renewal;

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
        services.AddScoped<EnrollmentPeerClient>();
        services.AddScoped<IEnrollmentPeerClient>(provider => provider.GetRequiredService<EnrollmentPeerClient>());
        services.AddScoped<EnrollmentPeerService>();
        services.AddScoped<EnrollmentIssuanceRouter>();
        if (services.Any(descriptor => descriptor.ServiceType == typeof(LocalVoterCredentialPublication)))
        {
            services.AddKeyedSingleton<EnrollmentNonces>(VoterContinuationAuthority.NONCE_KEY);
            services.AddKeyedSingleton<EnrollmentProbeReplay>(VoterContinuationAuthority.NONCE_KEY);
            services.AddKeyedSingleton<EnrollmentRateLimiter>(VoterContinuationAuthority.NONCE_KEY);
            services.AddScoped<IVoterContinuationAdministrator, VoterContinuationAdministrator>();
            services.AddScoped<VoterContinuationAuthority>();
            services.AddScoped<VoterContinuationIssuance>();
            services.AddScoped<VoterContinuationBind>();
            services.AddManagedVoterCredentialRotation();
            services.AddScoped<IVoterCredentialSignerClient>(provider => provider.GetRequiredService<EnrollmentPeerClient>());
            services.AddScoped<VoterCredentialPeerService>();
            services.AddScoped<VoterCredentialAdmission>();
            services.AddScoped<VoterCredentialAdmissionClient>();
            services.AddScoped<IVoterCredentialAdmissionClient>(provider => provider.GetRequiredService<VoterCredentialAdmissionClient>());
            services.AddScoped<IVoterCredentialRetirementClient>(provider => provider.GetRequiredService<VoterCredentialAdmissionClient>());
        }

        return services;
    }
}
