using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Tests.Infrastructure.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class PeerClientTests
{
    [Theory]
    [InlineData("Legacy", true)]
    [InlineData("Setup", true)]
    [InlineData("Legacy", false)]
    public async Task ProxyPreservesLegacyRequestsAndRejectsTypedBinding(string intent, bool typed)
    {
        // Arrange
        Trust trust = new();
        ClusterOptions options = new() { TlsBoundary = "NginxIsolatedHttp" };
        Server server = new(trust.Credential, "valid");
        using PeerClientHandler handler = new(Options.Create(options), trust, TimeProvider.System,
            new ClusterRuntimeState(new UnavailableClusterRecovery()),
            Options.Create(new InstallationOptions { InstallationIntent = intent })) { InnerHandler = server };
        using HttpClient client = new(handler);
        using HttpRequestMessage request = new(HttpMethod.Post, trust.Credential.Address);
        if (typed)
        {
            PeerCredential peer = trust.Credential;
            request.Options.Set(PeerClientHandler.CONNECTION_BINDING, new(peer.NodeId, peer.StorageGeneration,
                peer.Address, new string('B', 64), _ => throw new InvalidOperationException()));
        }

        // Act
        Exception? failure = await Record.ExceptionAsync(async () =>
        {
            using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        });

        // Assert
        if (typed)
        {
            Assert.IsType<HttpRequestException>(failure);
        }
        else
        {
            Assert.Null(failure);
        }

        Assert.Equal(typed ? 0 : 1, server.Calls);
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("challenge", false)]
    [InlineData("generation", false)]
    [InlineData("missing", false)]
    [InlineData("ambiguous", false)]
    [InlineData("timeout", false)]
    public async Task PeerResponseNeedsFreshMatchingIdentityWithoutCookie(string kind, bool accepted)
    {
        // Arrange
        Trust trust = new();
        ClusterOptions options = new() { ClusterId = Guid.NewGuid(), Generation = 1, NodeId = Guid.NewGuid(),
            StorageGeneration = Guid.NewGuid(), CredentialId = Guid.NewGuid() };
        Server server = new(trust.Credential, kind);
        using PeerClientHandler handler = new(Options.Create(options), trust, TimeProvider.System,
            new ClusterRuntimeState(new UnavailableClusterRecovery())) { InnerHandler = server };
        using HttpClient client = new(handler);
        using HttpRequestMessage request = new(HttpMethod.Get, "https://peer.example/internal/cluster/v1/health");
        request.Headers.Add("Cookie", "auth=secret");
        request.Headers.Authorization = new("Bearer", "credential");

        // Act
        Exception? failure = await Record.ExceptionAsync(async () =>
        {
            using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        });

        // Assert
        Assert.Equal(accepted, failure is null);
        Assert.False(server.HadCookie);
        Assert.False(server.HadAuthorization);
        Assert.Equal(1, server.Calls);
        if (failure is not null)
        {
            Assert.DoesNotContain("secret", failure.ToString());
            Assert.DoesNotContain("credential", failure.Message);
        }
    }

    [Theory]
    [InlineData("presented", true)]
    [InlineData("replacement-presented", true)]
    [InlineData("alternate-id", false)]
    [InlineData("missing-binding", false)]
    [InlineData("binding-from-previous-request", false)]
    [InlineData("changed-peer-before-http", false)]
    [InlineData("changed-record-after-http", false)]
    [InlineData("overlap-ended-after-http", false)]
    public async Task ManagedReplyRequiresTheCurrentRequestConnectionLeaf(string scenario, bool accepted)
    {
        // Arrange
        using EnrollmentCertificateFixture crypto = new();
        using EnrollmentCertificateFixture next = new();
        using X509Certificate2 leaf = crypto.Issue();
        using X509Certificate2 replacement = next.Issue();
        PeerCredential presented = new(crypto.Template.IssueId, crypto.Request.NodeId, crypto.Request.StorageGeneration,
            new Uri(crypto.Request.PeerUri), EnrollmentCertificates.Hash(leaf.RawData), true, false, crypto.Template.NotAfterUtc);
        PeerCredential alternate = presented with { CredentialId = next.Template.IssueId,
            Fingerprint = EnrollmentCertificates.Hash(replacement.RawData) };
        DateTimeOffset overlapEnd = crypto.Now.AddHours(24);
        TestClock clock = new(crypto.Now);
        Trust trust = new() { Credential = alternate, Presented = presented, Clock = clock, OverlapEnd = overlapEnd };
        ClusterOptions options = new() { ClusterId = Guid.NewGuid(), Generation = 1, NodeId = Guid.NewGuid(),
            StorageGeneration = Guid.NewGuid(), CredentialId = Guid.NewGuid() };
        PeerCredential claimed = presented;
        if (scenario is "alternate-id" or "replacement-presented")
        {
            claimed = alternate;
        }

        X509Certificate2 connectionLeaf = leaf;
        if (scenario == "replacement-presented")
        {
            connectionLeaf = replacement;
        }

        Server server = new(claimed, "valid");
        int requests = 0;
        server.BeforeReply = async (request, token) =>
        {
            requests++;
            Assert.Equal(HttpVersion.Version11, request.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionExact, request.VersionPolicy);
            Assert.True(request.Headers.ConnectionClose);
            if (scenario == "changed-peer-before-http")
            {
                trust.Credential = alternate with { NodeId = Guid.NewGuid() };
                trust.Presented = presented with { NodeId = trust.Credential.NodeId };
            }

            if (scenario != "missing-binding" && (scenario != "binding-from-previous-request" || requests == 1))
            {
                await PeerClientHandler.BindServerAsync(request, connectionLeaf, trust, clock, token);
            }

            if (scenario == "changed-record-after-http")
            {
                trust.Presented = presented with { Fingerprint = alternate.Fingerprint };
            }

            if (scenario == "overlap-ended-after-http")
            {
                clock.Now = overlapEnd;
            }
        };
        using PeerClientHandler handler = new(Options.Create(options), trust, clock,
            new ClusterRuntimeState(new UnavailableClusterRecovery()), Options.Create(new InstallationOptions { InstallationIntent = "Setup" }))
        { InnerHandler = server };
        using HttpClient client = new(handler);
        Uri uri = new(presented.Address, "/internal/cluster/v1/health");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        if (scenario == "binding-from-previous-request")
        {
            using HttpResponseMessage first = await client.GetAsync(uri, cancellationToken);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        // Act
        Exception? failure = await Record.ExceptionAsync(async () =>
        {
            using HttpResponseMessage response = await client.GetAsync(uri, cancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        });

        // Assert
        if (accepted)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.IsType<HttpRequestException>(failure);
        }
    }

    private class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }

    private class Trust : IPeerTrustSource
    {
        public PeerCredential Credential { get; set; } = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new Uri("https://peer.example/internal/raft"), new string('A', 64), true, false, DateTimeOffset.UtcNow.AddHours(1));
        public PeerCredential? Presented { get; set; }
        public TestClock? Clock { get; init; }
        public DateTimeOffset OverlapEnd { get; init; }

        public async ValueTask<PeerCredential?> FindAsync(Guid id, CancellationToken cancellationToken)
        {
            IReadOnlyList<PeerCredential> peers = await ListAsync(cancellationToken);
            return peers.SingleOrDefault(peer => peer.CredentialId == id);
        }

        public ValueTask<IReadOnlyList<PeerCredential>> ListAsync(CancellationToken cancellationToken)
        {
            if (Presented is not null && Clock?.Now < OverlapEnd)
            {
                return ValueTask.FromResult<IReadOnlyList<PeerCredential>>([Credential, Presented]);
            }

            return ValueTask.FromResult<IReadOnlyList<PeerCredential>>([Credential]);
        }
    }

    private class Server(PeerCredential credential, string kind) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool HadCookie { get; private set; }
        public bool HadAuthorization { get; private set; }
        public Func<HttpRequestMessage, CancellationToken, Task>? BeforeReply { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (BeforeReply is not null)
            {
                await BeforeReply(request, cancellationToken);
            }

            Calls++;
            HadCookie = request.Headers.Contains("Cookie");
            HadAuthorization = request.Headers.Authorization is not null;
            if (kind == "timeout")
            {
                throw new TaskCanceledException("Peer request timed out.");
            }

            PeerEnvelope sent = PeerHeaders.Decode(request.Headers.GetValues(PeerHeaders.ENVELOPE).Single())!;
            PeerEnvelope reply = sent with { FromNodeId = credential.NodeId, ToNodeId = sent.FromNodeId,
                StorageGeneration = credential.StorageGeneration, CredentialId = credential.CredentialId };
            if (kind == "challenge")
            {
                reply = reply with { Challenge = Guid.NewGuid() };
            }

            if (kind == "generation")
            {
                reply = reply with { Generation = 2 };
            }

            HttpResponseMessage response = new(HttpStatusCode.OK);
            if (kind != "missing")
            {
                response.Headers.Add(PeerHeaders.ENVELOPE, PeerHeaders.Encode(reply));
            }

            if (kind == "ambiguous")
            {
                response.Headers.Add(PeerHeaders.ENVELOPE, PeerHeaders.Encode(reply));
            }

            return response;
        }
    }
}
