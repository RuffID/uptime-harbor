using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Infrastructure.Telegram;
using UptimeHarbor.Infrastructure.Monitoring;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation;

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
        services.TryAddSingleton<IOnboardingAudit, OnboardingAudit>();
        services.TryAddSingleton<IEnrollmentAuthority, UnavailableEnrollmentAuthority>();
        services.TryAddSingleton<EnrollmentRateLimiter>();
        services.TryAddSingleton<GenesisWriterPermit>();
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
                if (provider.GetRequiredService<IOptions<InstallationOptions>>().Value.Setup)
                {
                    DynamicPeerTls credentials = provider.GetRequiredService<DynamicPeerTls>();
                    SocketsHttpHandler managed = new()
                    {
                        AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
                        ConnectTimeout = TimeSpan.FromSeconds(3), PooledConnectionLifetime = TimeSpan.Zero
                    };
                    SetupPeerAddressPolicy addresses = new(provider.GetRequiredService<IOptions<InstallationOptions>>().Value);
                    managed.ConnectCallback = addresses.ConnectAsync;
                    managed.SslOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) => credentials.Certificate;
                    managed.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    {
                        if (certificate is null || (errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
                        {
                            return false;
                        }

                        using X509Certificate2 candidate = new(certificate);
                        return credentials.Validate(candidate);
                    };
                    managed.PlaintextStreamFilter = async (context, token) =>
                    {
                        UptimeHarbor.Application.Installation.RuntimeGenerationContext.Verify();
                        Uri target = context.InitialRequestMessage.RequestUri
                            ?? throw new HttpRequestException("PeerServerPinMissing");
                        if (context.PlaintextStream is not System.Net.Security.SslStream tls || tls.RemoteCertificate is null)
                        {
                            throw new HttpRequestException("PeerServerTlsMissing");
                        }

                        using X509Certificate2 server = new(tls.RemoteCertificate);
                        IPeerTrustSource acceptedTrust = provider.GetRequiredService<IPeerTrustSource>();
                        if (!await PeerServerPin.MatchesAsync(target, server, acceptedTrust,
                            provider.GetRequiredService<TimeProvider>().GetUtcNow(), token))
                        {
                            throw new HttpRequestException("PeerServerPinRejected");
                        }

                        return context.PlaintextStream;
                    };
                    return managed;
                }

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
