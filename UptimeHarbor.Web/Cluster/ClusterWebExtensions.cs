using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Identity;

namespace UptimeHarbor.Web.Cluster;

public static class ClusterWebExtensions
{
    public static IServiceCollection AddClusterWeb(this IServiceCollection services)
    {
        services.AddSingleton<ClusterRaftHandle>();
        services.AddSingleton<ClusterPeerRuntime>();
        services.AddHostedService(provider => provider.GetRequiredService<ClusterPeerRuntime>());
        services.AddSingleton<ClusterHealthWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<ClusterHealthWorker>());
        services.AddScoped<ClusterBrowserDiagnostics>();
        services.AddScoped<MembershipCoordinator>();
        services.AddSingleton<IMembershipProtocol, DotNextMembershipProtocol>();
        services.AddSingleton<IConsensusWriter, ClusterConsensusWriter>();
        services.AddSingleton<RevisionDispatcher>();
        services.AddSingleton<ISenderConsensus, DeliverySenderConsensus>();
        services.AddSingleton<AcceptedDeliveryDispatcher>();
        services.AddHostedService<ClusterDeliveryWorker>();
        services.AddScoped<ClusterAccountBootstrapper>();
        services.AddSingleton<IRevisionTransport, ClusterRevisionTransport>();
        services.AddSingleton<PendingReplicationPump>();
        services.AddSingleton<ObservationReplicationPump>();
        services.AddSingleton<MonitoringCoordinator>();
        services.AddHostedService<ClusterRevisionWorker>();
        services.AddHostedService<ClusterSnapshotWorker>();
        services.AddHostedService<ClusterMonitoringWorker>();
        return services;
    }

    public static IApplicationBuilder UseClusterPeerBoundary(this IApplicationBuilder application)
    {
        return application.UseMiddleware<PeerBoundaryMiddleware>();
    }
}
