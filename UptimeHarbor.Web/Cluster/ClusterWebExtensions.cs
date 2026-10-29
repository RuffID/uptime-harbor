using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Web.Cluster;

public static class ClusterWebExtensions
{
    public static IServiceCollection AddClusterWeb(this IServiceCollection services)
    {
        services.AddSingleton<ClusterPeerRuntime>();
        services.AddHostedService(provider => provider.GetRequiredService<ClusterPeerRuntime>());
        services.AddHostedService<ClusterHealthWorker>();
        services.AddScoped<MembershipCoordinator>();
        services.AddSingleton<IMembershipProtocol, DotNextMembershipProtocol>();
        services.AddSingleton<IConsensusWriter, ClusterConsensusWriter>();
        services.AddSingleton<RevisionDispatcher>();
        services.AddSingleton<IRevisionTransport, ClusterRevisionTransport>();
        services.AddSingleton<PendingReplicationPump>();
        services.AddHostedService<ClusterRevisionWorker>();
        services.AddHostedService<ClusterSnapshotWorker>();
        return services;
    }

    public static IApplicationBuilder UseClusterPeerBoundary(this IApplicationBuilder application)
    {
        return application.UseMiddleware<PeerBoundaryMiddleware>();
    }
}
