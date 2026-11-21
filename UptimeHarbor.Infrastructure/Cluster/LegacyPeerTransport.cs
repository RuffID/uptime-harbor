using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Typed Direct использует отдельный пул. Обычные legacy RPC сохраняют прежний handler.</summary>
internal sealed class LegacyPeerTransport(X509Certificate2 certificate, IPeerTrustSource trust, TimeProvider time,
    LegacyPeerTransport.HandlerFactory createHandler, HttpMessageHandler ordinary)
    : PeerBoundTransport<X509Certificate2>(LOCAL_CERTIFICATE, trust, time, createHandler)
{
    private static readonly HttpRequestOptionsKey<X509Certificate2> LOCAL_CERTIFICATE = new("UptimeHarbor.LegacyLocalCertificate");
    private readonly HttpMessageInvoker ordinaryTransport = new(ordinary);

    public static HttpMessageHandler Create(ClusterOptions options, IPeerTrustSource trust, TimeProvider time, RevisionStateStore revisions)
    {
        X509Certificate2 local = X509Certificate2.CreateFromPemFile(options.ClientCertificatePath, options.ClientKeyPath);
        SocketsHttpHandler ordinary = CreateSocketsHandler(local);
        return options.TlsBoundary == "Direct"
            ? new LegacyPeerTransport(local, trust, time, (leaf, pooled, confirm) => CreateBoundHandler(leaf, pooled, confirm,
                () => revisions.Available ? revisions.Current : null, time), ordinary)
            : ordinary;
    }

    internal static SocketsHttpHandler CreateSocketsHandler(X509Certificate2 local)
    {
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
        return handler;
    }

    internal static SocketsHttpHandler CreateBoundHandler(X509Certificate2 local, bool pooled, ConfirmConnection confirm,
        Func<RevisionImage?>? readAccepted = null, TimeProvider? time = null)
    {
        SocketsHttpHandler handler = CreateSocketsHandler(local);
        if (!pooled)
        {
            handler.PooledConnectionLifetime = TimeSpan.Zero;
        }

        ConditionalWeakTable<SslStream, PeerServerPin.ChainProof> chains = new();
        handler.SslOptions.AllowTlsResume = false;
        handler.SslOptions.AllowRenegotiation = false;
        // SslStream проверяет имя и передаёт цепочку. Политика отзыва применяется после выбора доверия.
        X509ChainPolicy preliminary = CreateServerChainPolicy();
        preliminary.RevocationMode = X509RevocationMode.NoCheck;
        preliminary.DisableCertificateDownloads = true;
        handler.SslOptions.CertificateChainPolicy = preliminary;
        handler.SslOptions.CertificateRevocationCheckMode = preliminary.RevocationMode;
        handler.SslOptions.RemoteCertificateValidationCallback = (sender, remote, chain, errors) =>
        {
            if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None
                || sender is not SslStream stream || remote is null || chain is null)
            {
                return false;
            }

            using X509Certificate2 server = new(remote);
            PeerServerPin.ChainProof? proof = SelectServerTrust(server, chain, (time ?? TimeProvider.System).GetUtcNow(), readAccepted);
            if (proof is null)
            {
                return false;
            }

            chains.Add(stream, proof);
            return true;
        };
        handler.PlaintextStreamFilter = async (context, token) =>
        {
            if (context.PlaintextStream is not SslStream { IsMutuallyAuthenticated: true, RemoteCertificate: { } remote } tls
                || !chains.TryGetValue(tls, out PeerServerPin.ChainProof? chain))
            {
                throw new HttpRequestException("PeerServerTlsMissing");
            }

            chains.Remove(tls);
            using X509Certificate2 server = new(remote);
            await confirm(context.InitialRequestMessage, server, tls.LocalCertificate, token, chain);
            return context.PlaintextStream;
        };
        return handler;
    }

    internal static PeerServerPin.ChainProof? SelectServerTrust(X509Certificate2 server, X509Chain presented,
        DateTimeOffset now, Func<RevisionImage?>? readAccepted)
    {
        try
        {
            bool? dedicated = LegacyDirectPeerTrust.ValidateDedicated(server, readAccepted?.Invoke(), now, server: true);
            byte[] der = server.RawData;
            if (dedicated.HasValue)
            {
                if (!dedicated.Value)
                {
                    return null;
                }

                return new("dedicated:" + server.GetCertHashString(HashAlgorithmName.SHA256), checkedAt =>
                {
                    using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(der);
                    return LegacyDirectPeerTrust.ValidateDedicated(leaf, readAccepted?.Invoke(), checkedAt, server: true) == true;
                });
            }

            using X509Chain chain = new();
            X509ChainPolicy policy = CreateServerChainPolicy();
            policy.VerificationTime = now.UtcDateTime;
            policy.ExtraStore.AddRange(presented.ChainPolicy.ExtraStore);
            foreach (X509ChainElement item in presented.ChainElements)
            {
                policy.ExtraStore.Add(item.Certificate);
            }

            chain.ChainPolicy = policy;
            if (!chain.Build(server))
            {
                return null;
            }

            byte[][] certificates = chain.ChainElements.Select(element => element.Certificate.RawData).ToArray();
            string digest = ChainDigest(chain);
            return new(digest, checkedAt =>
            {
                using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(der);
                return LegacyDirectPeerTrust.ValidateDedicated(leaf, readAccepted?.Invoke(), checkedAt, server: true) is null
                    && ValidateServerChain(certificates, digest, checkedAt);
            });
        }
        catch (Exception error) when (error is CryptographicException or InvalidDataException or FormatException)
        {
            return null;
        }
    }

    private static X509ChainPolicy CreateServerChainPolicy()
    {
        X509ChainPolicy policy = new()
        {
            TrustMode = X509ChainTrustMode.System,
            RevocationMode = X509RevocationMode.Online,
            RevocationFlag = X509RevocationFlag.ExcludeRoot,
            UrlRetrievalTimeout = TimeSpan.FromSeconds(3)
        };
        policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        return policy;
    }

    private static string ChainDigest(X509Chain chain)
    {
        return string.Concat(chain.ChainElements.Select(element =>
            element.Certificate.GetCertHashString(HashAlgorithmName.SHA256)));
    }

    private static bool ValidateServerChain(byte[][] certificates, string digest, DateTimeOffset now)
    {
        using X509Chain chain = new();
        X509ChainPolicy policy = CreateServerChainPolicy();
        policy.VerificationTime = now.UtcDateTime;
        chain.ChainPolicy = policy;
        using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(certificates[0]);
        try
        {
            foreach (byte[] der in certificates.Skip(1))
            {
                policy.ExtraStore.Add(X509CertificateLoader.LoadCertificate(der));
            }

            return chain.Build(leaf) && ChainDigest(chain) == digest;
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            foreach (X509Certificate2 item in policy.ExtraStore)
            {
                item.Dispose();
            }
        }
    }

    protected override bool RequiresChainProof => true;

    protected override X509Certificate2 GetCertificate(X509Certificate2 local) => local;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Options.TryGetValue(PeerClientHandler.CONNECTION_BINDING, out _))
        {
            return ordinaryTransport.SendAsync(request, cancellationToken);
        }

        request.Options.Set(LOCAL_CERTIFICATE, certificate);
        return base.SendAsync(request, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ordinaryTransport.Dispose();
        }

        base.Dispose(disposing);
        if (disposing)
        {
            certificate.Dispose();
        }
    }
}
