using System.Security.Cryptography.X509Certificates;

namespace UptimeHarbor.Infrastructure.Cluster;

internal sealed class LegacyPeerTransport(X509Certificate2 certificate, SocketsHttpHandler handler) : DelegatingHandler(handler)
{
    public static HttpMessageHandler Create(ClusterOptions options)
    {
        X509Certificate2 local = X509Certificate2.CreateFromPemFile(options.ClientCertificatePath, options.ClientKeyPath);
        SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1)
        };
        handler.SslOptions.ClientCertificates = [local];
        handler.SslOptions.CertificateRevocationCheckMode = X509RevocationMode.Online;
        return new LegacyPeerTransport(local, handler);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            certificate.Dispose();
        }
    }
}
