using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Persistence.UnitOfWork;

namespace UptimeHarbor.Infrastructure.Installation;

public static class InstallationServiceCollectionExtensions
{
    public static IServiceCollection AddLocalInstallation(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<InstallationOptions>().Bind(configuration.GetSection(InstallationOptions.SECTION_NAME));
        services.TryAddSingleton<InstallationState>();
        services.TryAddSingleton<RuntimeWriteGate>();
        services.TryAddSingleton<DynamicPeerTls>();
        services.TryAddSingleton(new PreparedPeerEndpoint(false, string.Empty));
        services.AddScoped<ILocalInstallationRepository, LocalInstallationRepository>();
        services.AddScoped<ILocalOnboardingAttemptRepository, LocalOnboardingAttemptRepository>();
        services.AddScoped<ILocalSetupUnitOfWork, LocalSetupUnitOfWork>();
        services.AddScoped<IOnboardingProgressUnitOfWork, OnboardingProgressUnitOfWork>();
        services.AddScoped<IInstallationInventory, EfInstallationInventory>();
        services.AddScoped<ILocalSetupAccountStore, LocalSetupAccountStore>();
        services.TryAddSingleton<ISetupAuthority, UnavailableSetupAuthority>();
        services.TryAddSingleton<IManagedRuntimeAdmission, UnavailableManagedRuntimeAdmission>();
        if (configuration["Installation:InstallationIntent"] == "Setup")
        {
            services.AddSingleton<IOptions<ClusterOptions>, ActiveClusterOptions>();
            services.AddScoped<SetupPreflight>();
            services.AddSingleton<LocalSecretStore>();
            services.AddScoped<SetupInitialization>();
            services.AddScoped<SetupAccountLogin>();
            services.AddScoped<InstallationPreparation>();
            services.AddScoped<ISetupBotIdentity, SetupBotIdentity>();
            services.AddScoped<InstallationLifecycle>();
            services.AddScoped<OnboardingProgress>();
            services.AddScoped<SetupAuthorityCoordinator>();
        }

        return services;
    }
}
