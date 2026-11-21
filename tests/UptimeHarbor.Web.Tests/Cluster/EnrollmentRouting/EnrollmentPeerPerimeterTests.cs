using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Web.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

public class EnrollmentPeerPerimeterTests
{
    [Theory]
    [InlineData("NginxIsolatedHttp", "http", false, "trusted", 405)]
    [InlineData("NginxIsolatedHttp", "http", false, "unlisted", 401)]
    [InlineData("NginxIsolatedHttp", "http", false, "failed-mtls", 401)]
    [InlineData("NginxIsolatedHttp", "http", false, "missing-certificate", 401)]
    [InlineData("NginxIsolatedHttp", "http", false, "wrong-pin", 403)]
    [InlineData("NginxIsolatedHttp", "http", true, "trusted", 403)]
    [InlineData("NginxIsolatedHttp", "https", true, "trusted", 403)]
    [InlineData("Direct", "http", true, "trusted", 403)]
    [InlineData("Direct", "https", true, "trusted", 401)]
    public async Task ForwardedHeadersCannotAuthorizeRenewalAndLegacyProxyAuthenticationStillWorks(
        string boundary, string scheme, bool renewal, string proxy, int expected)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using EnrollmentRoutingFixture fixture = new();
        await fixture.InitializeAsync(token);
        EnrollmentRoutingNode target = fixture.Issuer;
        EnrollmentIssuerReference issuer = fixture.First.Store.Issuers.Values.Single();
        ClusterOptions options = target.Options;
        options.TlsBoundary = boundary;
        options.TrustedProxyAddresses = ["127.0.0.1"];
        options.ClientCaPath = Path.Combine(fixture.First.Root, "proxy-test-ca.cer");
        options.ClientCaFingerprint = issuer.CertificateDigest;
        await File.WriteAllBytesAsync(options.ClientCaPath, issuer.CertificateDer, token);
        PeerEnvelope envelope = EnrollmentRoutingSecurityTests.Peer(fixture, target);
        DefaultHttpContext context = new();
        context.Request.Method = "POST";
        context.Request.Scheme = scheme;
        context.Request.Host = new(new Uri(options.PublicUri).Authority);
        context.Request.Path = renewal ? EnrollmentPeerProtocol.RENEW : "/internal/cluster/v1/health";
        context.Request.ContentLength = 0;
        context.RequestAborted = token;
        context.Connection.LocalPort = options.PeerListenPort;
        context.Connection.RemoteIpAddress = IPAddress.Parse(proxy == "unlisted" ? "127.0.0.2" : "127.0.0.1");
        context.Request.Headers[PeerHeaders.ENVELOPE] = PeerHeaders.Encode(envelope);
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
        context.Request.Headers["Ssl-Client-Verify"] = proxy == "failed-mtls" ? "FAILED:certificate revoked" : "SUCCESS";
        if (proxy != "missing-certificate")
        {
            X509Certificate2 certificate = proxy == "wrong-pin" ? fixture.Leader.Certificate : fixture.Ingress.Certificate;
            context.Request.Headers["Ssl-Client-Cert"] = Uri.EscapeDataString(certificate.ExportCertificatePem());
        }

        PeerBoundaryMiddleware middleware = new(_ => throw new InvalidOperationException("Unexpected downstream request"),
            Microsoft.Extensions.Options.Options.Create(options));

        // Act
        await middleware.InvokeAsync(context, new AcceptedPeerTrustSource(fixture.State, fixture.First.Time),
            target.Readiness, fixture.First.Time, target.Runtime, target.View);

        // Assert
        Assert.Equal(expected, context.Response.StatusCode);
        Assert.Empty(context.Items);
        // Health returns 405 for POST only after authenticating and preparing the peer reply.
        Assert.Equal(expected == StatusCodes.Status405MethodNotAllowed, context.Response.Headers.ContainsKey(PeerHeaders.ENVELOPE));
    }

    [Fact]
    public async Task LegacyDirectRechecksBoundDedicatedCredentialOnEachRequest()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using VoterCredentialPeerFixture fixture = new();
        await fixture.InitializeAsync(token);
        using X509Certificate2 next = fixture.BindIssuerReplacement();
        RevisionImage accepted = fixture.Cluster.State.Current!;
        OperationalCredential replacement = accepted.Operations.Credentials.Values.Single(item =>
            item.Fingerprint == next.GetCertHashString(HashAlgorithmName.SHA256));
        EnrollmentRoutingNode target = fixture.Subject;
        int reached = 0;
        PeerBoundaryMiddleware middleware = new(_ =>
        {
            reached++;
            return Task.CompletedTask;
        }, Microsoft.Extensions.Options.Options.Create(target.Options));
        AcceptedPeerTrustSource trust = new(fixture.Cluster.State, fixture.Cluster.First.Time);

        // Act
        DefaultHttpContext valid = Request(replacement.Id);
        await middleware.InvokeAsync(valid, trust, target.Readiness, fixture.Cluster.First.Time, target.Runtime,
            target.View, revisions: fixture.Cluster.State);
        DefaultHttpContext wrongId = Request(fixture.Issuer.Options.CredentialId);
        await middleware.InvokeAsync(wrongId, trust, target.Readiness, fixture.Cluster.First.Time, target.Runtime,
            target.View, revisions: fixture.Cluster.State);
        fixture.Publish(accepted with
        {
            Operations = accepted.Operations with
            {
                Credentials = accepted.Operations.Credentials.SetItem(replacement.Id,
                    replacement with { RevokedAtUtc = fixture.Cluster.First.Time.Now })
            }
        });
        DefaultHttpContext revoked = Request(replacement.Id);
        await middleware.InvokeAsync(revoked, trust, target.Readiness, fixture.Cluster.First.Time, target.Runtime,
            target.View, revisions: fixture.Cluster.State);

        // Assert
        Assert.Equal(200, valid.Response.StatusCode);
        Assert.Equal(403, wrongId.Response.StatusCode);
        Assert.Equal(403, revoked.Response.StatusCode);
        Assert.Equal(1, reached);

        DefaultHttpContext Request(Guid credentialId)
        {
            ClusterOptions origin = fixture.Issuer.Options;
            DefaultHttpContext context = new();
            context.Request.Method = "POST";
            context.Request.Scheme = "https";
            context.Request.Host = new(new Uri(target.Options.PublicUri).Authority);
            context.Request.Path = EnrollmentPeerProtocol.ISSUE;
            context.Request.ContentLength = 0;
            context.RequestAborted = token;
            context.Connection.LocalPort = target.Options.PeerListenPort;
            context.Connection.ClientCertificate = next;
            PeerEnvelope envelope = new(origin.ClusterId, origin.Generation, origin.NodeId, target.Options.NodeId,
                origin.StorageGeneration, credentialId, Guid.NewGuid(), fixture.Cluster.First.Time.Now,
                origin.Compatibility, Guid.NewGuid(), fixture.Cluster.Term);
            context.Request.Headers[PeerHeaders.ENVELOPE] = PeerHeaders.Encode(envelope);

            return context;
        }
    }

    [Theory]
    [InlineData("valid", 200, true)]
    [InlineData("cookie", 403, false)]
    [InlineData("authorization", 403, false)]
    [InlineData("anonymous", 401, false)]
    [InlineData("browser-listener", 404, false)]
    [InlineData("nonvoter", 403, false)]
    [InlineData("arbitrary-sign", 404, false)]
    public async Task OnlyAcceptedVoterOnPeerListenerCanReachFixedTypedRoute(string mutation, int expected, bool reachesService)
    {
        // Arrange
        await using EnrollmentRoutingFixture fixture = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await fixture.InitializeAsync(token);
        EnrollmentRoutingNode target = fixture.Issuer;
        DefaultHttpContext context = new();
        context.Request.Method = "POST";
        context.Request.Scheme = "https";
        context.Request.Host = new(new Uri(target.Options.PublicUri).Authority);
        context.Request.Path = EnrollmentPeerProtocol.ISSUE;
        context.Request.ContentLength = 0;
        context.RequestAborted = token;
        context.Connection.LocalPort = target.Options.PeerListenPort;
        context.Connection.ClientCertificate = fixture.Ingress.Certificate;
        context.Request.Headers[PeerHeaders.ENVELOPE] = PeerHeaders.Encode(EnrollmentRoutingSecurityTests.Peer(fixture, target));
        if (mutation == "cookie")
        {
            context.Request.Headers.Cookie = "auth=browser-cookie";
        }
        else if (mutation == "authorization")
        {
            context.Request.Headers.Authorization = "Bearer browser-token";
        }
        else if (mutation == "anonymous")
        {
            context.Request.Headers.Remove(PeerHeaders.ENVELOPE);
        }
        else if (mutation == "browser-listener")
        {
            context.Connection.LocalPort = 443;
        }
        else if (mutation == "nonvoter")
        {
            RevisionImage image = fixture.State.Current!;
            OperationalNode node = image.Operations.Nodes[fixture.Ingress.Options.NodeId];
            fixture.State.Publish(image with { Operations = image.Operations with
            {
                Nodes = image.Operations.Nodes.SetItem(node.NodeId, node with { Voting = false })
            } });
        }
        else if (mutation == "arbitrary-sign")
        {
            context.Request.Path = EnrollmentPeerProtocol.PREFIX + "/sign";
        }

        bool called = false;
        PeerBoundaryMiddleware middleware = new(_ =>
        {
            called = true;
            return Task.CompletedTask;
        }, Microsoft.Extensions.Options.Options.Create(target.Options),
            Microsoft.Extensions.Options.Options.Create(target.Local), target.Installation);
        RuntimeWriteGate writes = new();
        writes.Open(target.Installation.Current!.RuntimeGeneration);

        // Act
        await middleware.InvokeAsync(context, new AcceptedPeerTrustSource(fixture.State, fixture.First.Time), target.Readiness,
            fixture.First.Time, target.Runtime, target.View, target.Tls, writes);

        // Assert
        Assert.Equal(expected, context.Response.StatusCode);
        Assert.Equal(reachesService, called);
        Assert.Equal(0, context.Response.Headers.SetCookie.Count);
        if (called)
        {
            Assert.IsType<PeerEnvelope>(context.Items[typeof(PeerEnvelope)]);
        }
    }
}
