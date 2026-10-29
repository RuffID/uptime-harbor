using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Infrastructure.Telegram;

namespace UptimeHarbor.Infrastructure.Cluster;

public static class ClusterServiceCollectionExtensions
{
    public static IServiceCollection AddClusterInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<ClusterOptions>().Bind(configuration.GetSection(ClusterOptions.SECTION_NAME)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ClusterOptions>, ClusterOptionsValidator>();
        services.TryAddSingleton<IRecoveryAdmission, UnavailableRecoveryAdmission>();
        services.TryAddSingleton<RevisionStateStore>();
        services.TryAddSingleton<IAcceptedAccountReader>(provider => provider.GetRequiredService<RevisionStateStore>());
        services.TryAddSingleton<RevisionReducer>();
        services.TryAddSingleton<IRevisionProjectionStore, EfRevisionProjectionStore>();
        services.TryAddSingleton<RevisionApplyEngine>();
        services.TryAddSingleton<RevisionSnapshotStore>();
        services.TryAddSingleton<RevisionClusterRecovery>();
        services.TryAddSingleton<IClusterRecovery>(provider => provider.GetRequiredService<RevisionClusterRecovery>());
        services.TryAddSingleton<IRevisionAudit, RevisionAudit>();
        services.TryAddSingleton<IPeerTrustSource, UnavailablePeerTrustSource>();
        services.TryAddSingleton<ISenderConsensus, UnavailableSenderConsensus>();
        services.TryAddSingleton<IClusterAdministrationState, UnavailableClusterAdministrationState>();
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
        services.AddTransient<PeerClientHandler>();
        services.AddHttpClient(PeerClientHandler.CLIENT_NAME, client => client.Timeout = TimeSpan.FromSeconds(10))
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(provider =>
            {
                ClusterOptions options = provider.GetRequiredService<IOptions<ClusterOptions>>().Value;
                X509Certificate2 certificate = X509Certificate2.CreateFromPemFile(
                    options.ClientCertificatePath, options.ClientKeyPath);
                SocketsHttpHandler handler = new()
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    UseProxy = false,
                    ConnectTimeout = TimeSpan.FromSeconds(3),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(1)
                };
                handler.SslOptions.ClientCertificates = [certificate];
                handler.SslOptions.CertificateRevocationCheckMode = X509RevocationMode.Online;
                return handler;
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
