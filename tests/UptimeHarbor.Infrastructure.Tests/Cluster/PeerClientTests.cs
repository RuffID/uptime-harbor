using System.Net;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class PeerClientTests
{
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

    private class Trust : IPeerTrustSource
    {
        public PeerCredential Credential { get; } = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new Uri("https://peer.example/internal/raft"), new string('A', 64), true, false, DateTimeOffset.UtcNow.AddHours(1));

        public ValueTask<PeerCredential?> FindAsync(Guid id, CancellationToken cancellationToken)
        {
            if (id == Credential.CredentialId)
            {
                return ValueTask.FromResult<PeerCredential?>(Credential);
            }

            return ValueTask.FromResult<PeerCredential?>(null);
        }

        public ValueTask<IReadOnlyList<PeerCredential>> ListAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<PeerCredential>>([Credential]);
        }
    }

    private class Server(PeerCredential credential, string kind) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool HadCookie { get; private set; }
        public bool HadAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
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

            return Task.FromResult(response);
        }
    }
}
