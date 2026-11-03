using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Identity;
using UptimeHarbor.Application.Cluster.Onboarding;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Web.Cluster;

public static class ClusterWebExtensions
{
    public static IServiceCollection AddClusterWeb(this IServiceCollection services, IConfiguration? configuration = null)
    {
        services.AddOptions<InstallationOptions>();
        services.TryAddSingleton<InstallationState>();
        services.TryAddSingleton<RuntimeWriteGate>();
        services.TryAddSingleton<IManagedRuntimeAdmission, UnavailableManagedRuntimeAdmission>();
        services.AddSingleton<ClusterRaftHandle>();
        services.AddSingleton<ClusterPeerRuntime>();
        services.AddSingleton<ClusterHealthWorker>();
        services.AddScoped<ClusterBrowserDiagnostics>();
        services.AddScoped<MembershipCoordinator>();
        services.AddSingleton<IClusterAdministrationState, AcceptedClusterAdministrationState>();
        services.AddSingleton<IMembershipProtocol, DotNextMembershipProtocol>();
        services.AddSingleton<IConsensusWriter, ClusterConsensusWriter>();
        services.TryAddSingleton<IEnrollmentSigningAuthorization, EnrollmentSigningAuthorization>();
        services.TryAddSingleton<IEnrollmentReadBarrier, EnrollmentReadBarrier>();
        if (configuration?["Installation:InstallationIntent"] == "Setup")
        {
            services.TryAddSingleton<IOnlineGrantSigningAuthorization, OnlineGrantSigningAuthorization>();
            services.AddScoped<ManagedBootstrapLifecycle>();
        }
        services.AddSingleton<RevisionDispatcher>();
        services.AddSingleton<IOnboardingAcceptance, OnboardingAcceptance>();
        services.AddRateLimiter(options => options.AddPolicy("onboarding-administrator", context =>
            RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                })));
        services.AddSingleton<ClusterLifecycleWorker>();
        services.AddSingleton<ISenderConsensus, DeliverySenderConsensus>();
        services.AddSingleton<AcceptedDeliveryDispatcher>();
        services.AddSingleton<ClusterDeliveryWorker>();
        services.AddScoped<ClusterAccountBootstrapper>();
        services.AddSingleton<IRevisionTransport, ClusterRevisionTransport>();
        services.AddSingleton<PendingReplicationPump>();
        services.AddSingleton<ObservationReplicationPump>();
        services.AddSingleton<MonitoringCoordinator>();
        services.AddSingleton<ClusterRevisionWorker>();
        services.AddSingleton<ClusterSnapshotWorker>();
        services.AddSingleton<ClusterMonitoringWorker>();
        services.AddSingleton<ManagedWorkerSupervisor>();
        services.AddSingleton<IManagedWorkerCatalog, ManagedWorkerCatalog>();
        services.AddSingleton<UptimeHarbor.Application.Installation.IInstallationRuntime>(provider => provider.GetRequiredService<ManagedWorkerSupervisor>());
        services.AddHostedService(provider => provider.GetRequiredService<ManagedWorkerSupervisor>());
        return services;
    }

    public static IApplicationBuilder UseClusterPeerBoundary(this IApplicationBuilder application)
    {
        return application.UseMiddleware<PeerBoundaryMiddleware>();
    }
}
