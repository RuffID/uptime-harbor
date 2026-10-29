using UptimeHarbor.Application.Cluster;

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
        return services;
    }

    public static IApplicationBuilder UseClusterPeerBoundary(this IApplicationBuilder application)
    {
        return application.UseMiddleware<PeerBoundaryMiddleware>();
    }
}
