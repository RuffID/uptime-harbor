using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Infrastructure.Telegram;
using UptimeHarbor.Infrastructure.Monitoring;

namespace UptimeHarbor.Infrastructure.Cluster;

public static class ClusterServiceCollectionExtensions
{
    public static IServiceCollection AddClusterInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<ClusterOptions>().Bind(configuration.GetSection(ClusterOptions.SECTION_NAME)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ClusterOptions>, ClusterOptionsValidator>();
        services.AddOptions<ClusterControlOptions>().Bind(configuration.GetSection("ClusterControl"));
        services.TryAddSingleton<ClusterLocalControl>();
        services.AddDataProtection();
        services.TryAddSingleton<IRecoveryAdmission, SignedRecoveryAdmission>();
        services.TryAddSingleton<IClusterLocalInitialization, ClusterLocalInitialization>();
        services.TryAddSingleton<RevisionStateStore>();
        services.TryAddSingleton<IAcceptedAccountReader>(provider => provider.GetRequiredService<RevisionStateStore>());
        services.TryAddSingleton<RevisionReducer>();
        services.TryAddSingleton<IRevisionProjectionStore, EfRevisionProjectionStore>();
        services.TryAddSingleton<RevisionApplyEngine>();
        services.TryAddSingleton<RevisionSnapshotStore>();
        services.TryAddSingleton<RevisionClusterRecovery>();
        services.TryAddSingleton<IClusterRecovery>(provider => provider.GetRequiredService<RevisionClusterRecovery>());
        services.TryAddSingleton<IRevisionAudit, RevisionAudit>();
        services.TryAddSingleton<IMonitoringAudit, MonitoringAudit>();
        services.TryAddSingleton<IDeliveryAudit, DeliveryAudit>();
        services.TryAddSingleton<IPeerTrustSource, AcceptedPeerTrustSource>();
        services.TryAddSingleton<ISenderConsensus, UnavailableSenderConsensus>();
        services.AddSingleton<IClusterAudit, ClusterAudit>();
        services.AddSingleton<NodeReadiness>();
        services.AddSingleton<SenderAuthorityGate>();
        services.AddSingleton<LeaderFailureWindow>();
        services.AddSingleton<ClusterRuntimeState>();
        services.AddSingleton<IClusterStateView>(provider => provider.GetRequiredService<ClusterRuntimeState>());
        services.AddSingleton<ClusterStateMachine>();
        services.AddSingleton<ClusterDatabaseProbe>();
        services.AddSingleton<TelegramHealthProbe>();
        services.AddSingleton<MonitoringProgressWatchdog>();
        services.AddSingleton<ObservationCaptureGate>();
        services.AddSingleton<PeerObservationCapture>();
        services.AddSingleton<ClusterMonitoringCycle>();
        services.AddTransient<PeerClientHandler>();
        services.AddHttpClient(PeerClientHandler.CLIENT_NAME, client => client.Timeout = TimeSpan.FromSeconds(10))
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(provider =>
            {
                ClusterOptions options = provider.GetRequiredService<IOptions<ClusterOptions>>().Value;
                return LegacyPeerTransport.Create(options);
            })
            .AddHttpMessageHandler<PeerClientHandler>();
        services.AddHttpClient(TelegramHealthProbe.CLIENT_NAME, client =>
            {
                client.BaseAddress = new Uri("https://api.telegram.org/");
                client.Timeout = TimeSpan.FromSeconds(5);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
            .RemoveAllLoggers();
        return services;
    }
}
