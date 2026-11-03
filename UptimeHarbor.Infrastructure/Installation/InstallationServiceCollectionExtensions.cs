using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Persistence.UnitOfWork;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

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
            services.Replace(ServiceDescriptor.Scoped<ISetupAuthority, SetupAuthority>());
            services.Replace(ServiceDescriptor.Singleton<IManagedRuntimeAdmission, ManagedRuntimeAdmission>());
            services.TryAddSingleton<ManagedAdmissionState>();
            services.TryAddSingleton<OnlineEnableState>();
            services.TryAddSingleton<SignedRecoveryAdmission>();
            services.Replace(ServiceDescriptor.Singleton<UptimeHarbor.Application.Cluster.IRecoveryAdmission, ManagedRecoveryAdmission>());
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
            services.AddScoped<IEnrollmentSigningRepository, EnrollmentSigningRepository>();
            services.AddScoped<IEnrollmentSigningUnitOfWork, EnrollmentSigningUnitOfWork>();
            services.AddScoped<EnrollmentSigningJournal>();
            services.TryAddSingleton<EnrollmentSigningLock>();
            services.TryAddSingleton<EnrollmentNonces>();
            services.TryAddSingleton<EnrollmentExchangeGate>();
            services.TryAddSingleton<EnrollmentAuthority>();
            services.Replace(ServiceDescriptor.Singleton<IEnrollmentAuthority>(provider => provider.GetRequiredService<EnrollmentAuthority>()));
            services.AddScoped<EnrollmentDescriptors>();
            services.AddScoped<EnrollmentCommandJournal>();
            services.AddScoped<EnrollmentGateway>();
            services.TryAddSingleton<EnrollmentTransport>();
            services.TryAddSingleton(provider => new EnrollmentEndpoints(new SetupPeerAddressPolicy(
                provider.GetRequiredService<IOptions<InstallationOptions>>().Value)));
            services.AddScoped<JoinEnrollmentPreparation>();
            services.AddScoped<JoinEnrollmentAuthority>();
            services.AddScoped<EnrollmentTrustInstallation>();
            services.TryAddSingleton<EnrollmentProbeSession>();
            services.TryAddSingleton<EnrollmentProbeReplay>();
            services.TryAddSingleton<EnrollmentPeerProbeTransport>();
            services.AddScoped<EnrollmentPeerProbes>();
            services.TryAddSingleton<EnrollmentIssuerSigningLock>();
            services.AddScoped<EnrollmentSigner>();
            services.AddScoped<GenesisPreparation>();
            services.AddScoped<GenesisRuntimePreparation>();
            services.TryAddSingleton<ManagedStorageSeals>();
            services.AddScoped<ManagedRestartPreparation>();
            services.AddScoped<OnlineGrantSigner>();
            services.AddScoped<OnlineProcessRenewalSigner>();
        }

        return services;
    }
}
