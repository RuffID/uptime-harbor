using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Web.Cluster;

public static class ClusterListenerConfiguration
{
    public static WebApplicationBuilder ConfigureClusterListeners(this WebApplicationBuilder builder)
    {
        ClusterOptions options = new();
        builder.Configuration.GetSection(ClusterOptions.SECTION_NAME).Bind(options);
        if (builder.Configuration["Installation:InstallationIntent"] is not (null or "Legacy"))
        {
            throw new InvalidOperationException("Only Installation:InstallationIntent=Legacy is supported. Configure the cluster node manually.");
        }

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
        new PeerCertificateValidator().EnsureLocalCertificate(options, TimeProvider.System.GetUtcNow());
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
                        endpoint.HttpsOptions.OnAuthenticate = (_, ssl) =>
                        {
                            ssl.AllowTlsResume = false;
                            ssl.AllowRenegotiation = false;
                            ssl.CertificateChainPolicy = new X509ChainPolicy
                            {
                                DisableCertificateDownloads = true,
                                RevocationMode = X509RevocationMode.NoCheck
                            };
                        };
                        endpoint.HttpsOptions.ClientCertificateValidation = (certificate, _, _) =>
                        {
                            RevisionStateStore revisions = server.ApplicationServices.GetRequiredService<RevisionStateStore>();
                            return LegacyDirectPeerTrust.ValidateClient(certificate, options,
                                revisions.Available ? revisions.Current : null, DateTimeOffset.UtcNow);
                        };
                    }
                });
        });

        return builder;
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
