using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Web.Cluster;

public static class ClusterListenerConfiguration
{
    public static WebApplicationBuilder ConfigureClusterListeners(this WebApplicationBuilder builder)
    {
        ClusterOptions options = new();
        builder.Configuration.GetSection(ClusterOptions.SECTION_NAME).Bind(options);
        if (!options.Enabled)
        {
            return builder;
        }

        ValidateOptionsResult validation = new ClusterOptionsValidator().Validate(null, options);
        if (validation.Failed)
        {
            throw new OptionsValidationException(ClusterOptions.SECTION_NAME, typeof(ClusterOptions), validation.Failures);
        }

        ValidateEndpoint(options, builder.Configuration["Kestrel:Endpoints:Peer:Url"],
            builder.Configuration["Kestrel:Endpoints:Browser:Url"]);
        ValidateLocalCertificate(options);
        builder.WebHost.ConfigureKestrel((context, server) =>
        {
            server.Configure(context.Configuration.GetSection("Kestrel"), reloadOnChange: false)
                .Endpoint("Peer", endpoint =>
                {
                    if (options.TlsBoundary == "Direct")
                    {
                        if (!endpoint.IsHttps)
                        {
                            throw new InvalidOperationException("Peer listener requires HTTPS.");
                        }

                        endpoint.HttpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                        endpoint.HttpsOptions.ClientCertificateValidation = (certificate, _, _) =>
                        {
                            using X509Certificate2 root = X509CertificateLoader.LoadCertificateFromFile(options.ClientCaPath);
                            return new PeerCertificateValidator().ValidateChain(certificate, root, options, DateTimeOffset.UtcNow);
                        };
                    }
                });
        });

        return builder;
    }

    private static void ValidateLocalCertificate(ClusterOptions options)
    {
        using X509Certificate2 root = X509CertificateLoader.LoadCertificateFromFile(options.ClientCaPath);
        using X509Certificate2 certificate = X509Certificate2.CreateFromPemFile(
            options.ClientCertificatePath, options.ClientKeyPath);
        PeerCredential local = new(options.CredentialId, options.NodeId, options.StorageGeneration,
            new Uri(options.PublicUri), certificate.GetCertHashString(HashAlgorithmName.SHA256),
            false, false, DateTimeOffset.MaxValue);
        if (!certificate.HasPrivateKey || !new PeerCertificateValidator().Validate(certificate, root,
            options, local, DateTimeOffset.UtcNow))
        {
            throw new InvalidOperationException("Local peer certificate does not match the configured cluster identity.");
        }
    }

    public static void ValidateEndpoint(ClusterOptions options, string? peerUrl, string? browserUrl)
    {
        if (!Uri.TryCreate(peerUrl, UriKind.Absolute, out Uri? peer)
            || !Uri.TryCreate(browserUrl, UriKind.Absolute, out Uri? browser)
            || peer.Port != options.PeerListenPort || browser.Port == peer.Port
            || peer.UserInfo.Length != 0 || peer.Query.Length != 0 || peer.Fragment.Length != 0
            || peer.AbsolutePath != "/" || browser.AbsolutePath != "/"
            || browser.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("Separate Kestrel Peer and Browser URLs are required.");
        }

        string scheme = Uri.UriSchemeHttps;
        if (options.TlsBoundary == "NginxIsolatedHttp")
        {
            scheme = Uri.UriSchemeHttp;
        }

        if (peer.Scheme != scheme)
        {
            throw new InvalidOperationException("Peer listener scheme does not match the TLS boundary.");
        }
    }
}
