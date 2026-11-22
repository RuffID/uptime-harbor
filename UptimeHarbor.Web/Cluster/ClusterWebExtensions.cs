using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Web.Cluster;

public static class ClusterWebExtensions
{
    public static IServiceCollection AddClusterWeb(this IServiceCollection services)
    {
        services.TryAddSingleton<RuntimeWriteGate>();
        services.AddSingleton<ClusterRaftHandle>();
        services.AddSingleton<ClusterPeerRuntime>();
        services.AddSingleton<ClusterHealthWorker>();
        services.AddSingleton<ISenderCandidateHealth>(services => services.GetRequiredService<ClusterHealthWorker>());
        services.AddScoped<ClusterBrowserDiagnostics>();
        services.AddScoped<MembershipCoordinator>();
        services.AddSingleton<IClusterAdministrationState, AcceptedClusterAdministrationState>();
        services.AddSingleton<IMembershipProtocol, DotNextMembershipProtocol>();
        services.AddSingleton<IConsensusWriter, ClusterConsensusWriter>();
        services.AddSingleton<RevisionDispatcher>();
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
        services.AddHostedService<RuntimeWorkerSupervisor>();
        return services;
    }

    public static IApplicationBuilder UseClusterPeerBoundary(this IApplicationBuilder application)
    {
        return application.UseMiddleware<PeerBoundaryMiddleware>();
    }
}
