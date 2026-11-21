using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Выбирает транспорт по тому же снимку, из которого построен envelope.</summary>
internal sealed class ManagedPeerTransport(DynamicPeerTls credentials, IPeerTrustSource trust, TimeProvider time,
    ManagedPeerTransport.HandlerFactory createHandler) : PeerBoundTransport<DynamicPeerTls.Snapshot>(
        PeerClientHandler.LOCAL_CREDENTIAL, trust, time, createHandler)
{
    protected override X509Certificate2 GetCertificate(DynamicPeerTls.Snapshot snapshot) => snapshot.Certificate;

    protected override bool IsUsable(DynamicPeerTls.Snapshot snapshot) => credentials.IsUsable(snapshot);

    public static ManagedPeerTransport Create(DynamicPeerTls credentials, InstallationOptions options,
        IPeerTrustSource trust, TimeProvider time)
    {
        SetupPeerAddressPolicy addresses = new(options);
        return new(credentials, trust, time, (snapshot, pooled, confirm) =>
        {
            SocketsHttpHandler handler = new()
            {
                AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(3),
                ConnectCallback = addresses.ConnectAsync
            };
            if (!pooled)
            {
                handler.PooledConnectionLifetime = TimeSpan.Zero;
            }

            handler.SslOptions.AllowTlsResume = false;
            handler.SslOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) =>
            {
                if (!credentials.IsUsable(snapshot))
                {
                    throw new HttpRequestException("PeerLocalCredentialChanged");
                }

                return snapshot.Certificate;
            };
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
                {
                    return false;
                }

                using X509Certificate2 candidate = new(certificate);
                return credentials.Validate(snapshot, candidate);
            };
            handler.PlaintextStreamFilter = async (context, token) =>
            {
                if (context.PlaintextStream is not SslStream tls || tls.RemoteCertificate is null)
                {
                    throw new HttpRequestException("PeerServerTlsMissing");
                }

                using X509Certificate2 server = new(tls.RemoteCertificate);

                await confirm(context.InitialRequestMessage, server, tls.LocalCertificate, token);

                return context.PlaintextStream;
            };
            return handler;
        });
    }
}
