using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class ManagedPeerPoolTests
{
    [Fact]
    public async Task ProtectedFreshCallSeedsOnePool_AndReuseConfirmsEveryRequest()
    {
        // Arrange
        using Scenario scenario = new();
        await scenario.SendAsync(typed: false);

        // Act
        await scenario.SendAsync();
        await scenario.SendAsync();

        // Assert
        Assert.Equal(1, scenario.PoolCount);
        Assert.Equal(2, scenario.Handshakes);
        Assert.Equal(3, scenario.Sends);
        Assert.All(scenario.Confirmed, id => Assert.Equal(scenario.Old.CredentialId, id));
        Assert.Equal(3, scenario.Confirmed.Count);
    }

    [Theory]
    [InlineData("new-handshake", 2)]
    [InlineData("retired-before-send", 2)]
    [InlineData("retired-after-send", 3)]
    [InlineData("certificate-expired", 2)]
    public async Task StalePinRejectsOneCall_AndNextCallObservesIndependently(string change, int sendsAtFailure)
    {
        // Arrange
        using Scenario scenario = new();
        await scenario.SendAsync();
        await scenario.SendAsync();
        scenario.Offered = scenario.Next;
        if (change == "new-handshake")
        {
            scenario.Reconnect = true;
        }
        else if (change == "retired-before-send")
        {
            scenario.RetireOld();
        }
        else if (change == "retired-after-send")
        {
            scenario.OnSend = scenario.RetireOld;
        }
        else
        {
            scenario.Clock.Now = scenario.OldExpires;
        }

        // Act
        await Assert.ThrowsAsync<HttpRequestException>(() => scenario.SendAsync());

        // Assert
        Assert.Equal(sendsAtFailure, scenario.Sends);
        Assert.Equal(1, scenario.PoolCount);
        Assert.Equal(1, scenario.DisposedPools);

        // Act
        await scenario.SendAsync();
        await scenario.SendAsync();

        // Assert
        Assert.Equal(2, scenario.PoolCount);
        Assert.Equal(sendsAtFailure + 2, scenario.Sends);
        Assert.Equal(scenario.Next.CredentialId, scenario.Confirmed.Last());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplyIdOrWrongLocalLeafCannotReplacePin(bool wrongLocalLeaf)
    {
        // Arrange
        using Scenario scenario = new();
        await scenario.SendAsync();
        await scenario.SendAsync();
        scenario.Reconnect = wrongLocalLeaf;
        scenario.WrongLocal = wrongLocalLeaf;
        if (!wrongLocalLeaf)
        {
            scenario.ReplyId = scenario.Next.CredentialId;
        }

        // Act
        await Assert.ThrowsAsync<HttpRequestException>(() => scenario.SendAsync());

        // Assert
        Assert.Equal(wrongLocalLeaf ? 2 : 3, scenario.Sends);
        Assert.Equal(0, scenario.DisposedPools);
        Assert.Equal(1, scenario.PoolCount);

        // Act
        scenario.WrongLocal = false;
        scenario.ReplyId = null;
        await scenario.SendAsync();

        // Assert
        Assert.Equal(scenario.Old.CredentialId, scenario.Confirmed.Last());
        Assert.Equal(1, scenario.PoolCount);
    }

    [Fact]
    public async Task UnacceptedReplacementCannotInvalidateAuthenticatedPin()
    {
        // Arrange
        using Scenario scenario = new();
        await scenario.SendAsync();
        await scenario.SendAsync();
        scenario.RetireNext();
        scenario.Offered = scenario.Next;
        scenario.Reconnect = true;

        // Act
        await Assert.ThrowsAsync<HttpRequestException>(() => scenario.SendAsync());

        // Assert
        Assert.Equal(2, scenario.Sends);
        Assert.Equal(0, scenario.DisposedPools);

        // Act
        scenario.Offered = scenario.Old;
        await scenario.SendAsync();

        // Assert
        Assert.Equal(1, scenario.PoolCount);
        Assert.Equal(scenario.Old.CredentialId, scenario.Confirmed.Last());
    }

    [Fact]
    public async Task RacingFreshObservationsKeepTheirOwnIdentity_AndFirstConfirmationPinsPool()
    {
        // Arrange
        using Scenario scenario = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.BeforeHandshake = async (peer, token) =>
        {
            if (peer == scenario.Old)
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }
        };

        // Act
        Task first = scenario.SendAsync();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        scenario.Offered = scenario.Next;
        await scenario.SendAsync();
        release.SetResult();
        await first;
        scenario.BeforeHandshake = null;
        await scenario.SendAsync();
        await scenario.SendAsync();

        // Assert
        Assert.Equal([scenario.Next.CredentialId, scenario.Old.CredentialId,
            scenario.Next.CredentialId, scenario.Next.CredentialId, scenario.Next.CredentialId], scenario.Confirmed);
        Assert.Equal(3, scenario.Handshakes);
        Assert.Equal(1, scenario.PoolCount);
    }

    [Fact]
    public async Task LateObservationFromRetiredSlotCannotSeedReplacementSlot()
    {
        // Arrange
        using Scenario scenario = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.BeforeHandshake = async (peer, token) =>
        {
            if (peer == scenario.Old)
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }
        };
        Task late = scenario.SendAsync();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        scenario.Offered = scenario.Next;
        await scenario.SendAsync();
        await scenario.SendAsync();
        scenario.RetireNext();

        // Act
        await Assert.ThrowsAsync<HttpRequestException>(() => scenario.SendAsync());
        release.SetResult();
        await late;
        scenario.BeforeHandshake = null;
        scenario.Offered = scenario.Old;
        await scenario.SendAsync();

        // Assert
        Assert.Equal(1, scenario.PoolCount);
        Assert.Equal(1, scenario.DisposedPools);

        // Act
        await scenario.SendAsync();

        // Assert
        Assert.Equal(2, scenario.PoolCount);
        Assert.Equal(scenario.Old.CredentialId, scenario.Confirmed.Last());
    }

    [Fact]
    public async Task LocalGenerationChangeRejectsInFlightReply_AndNewSnapshotHasSeparatePool()
    {
        // Arrange
        using Scenario scenario = new();
        await scenario.SendAsync();
        await scenario.SendAsync();
        scenario.OnSend = scenario.NextLocalGeneration;

        // Act
        await Assert.ThrowsAsync<HttpRequestException>(() => scenario.SendAsync());
        scenario.Offered = scenario.Next;
        await scenario.SendAsync();
        await scenario.SendAsync();

        // Assert
        Assert.Equal(2, scenario.PoolCount);
        Assert.Equal(4, scenario.Handshakes);
        Assert.Equal(scenario.Next.CredentialId, scenario.Confirmed.Last());
        Assert.NotEqual(scenario.HandshakeLocals[0], scenario.HandshakeLocals[^1]);
        Assert.Equal(scenario.HandshakeLocals[2], scenario.HandshakeLocals[3]);
    }

    [Fact]
    public async Task GenerationChangeBeforeDispatchRejectsPreparedBinding()
    {
        // Arrange
        using Scenario scenario = new();
        await scenario.SendAsync();
        await scenario.SendAsync();

        // Act
        await Assert.ThrowsAsync<HttpRequestException>(() => scenario.SendAsync(switchBeforeDispatch: true));

        // Assert
        Assert.Equal(2, scenario.Sends);
        Assert.Equal(2, scenario.Handshakes);
        Assert.Equal(1, scenario.PoolCount);
    }

    internal class Scenario : IPeerTrustSource, IDisposable
    {
        private readonly bool legacy;
        private readonly InstallationState state = new();
        private readonly DynamicPeerTls tls;
        private readonly X509Certificate2 oldLeaf;
        private readonly X509Certificate2 nextLeaf;
        private readonly X509Certificate2 localLeaf;
        private readonly X509Certificate2 nextLocalLeaf;
        private readonly HttpMessageInvoker client;
        private IReadOnlyList<PeerCredential> accepted;
        public TestClock Clock { get; } = new();
        public PeerCredential Old { get; }
        public PeerCredential Next { get; }
        public PeerCredential Offered { get; set; }
        public DateTimeOffset OldExpires { get; }
        public bool Reconnect { get; set; }
        public bool WrongLocal { get; set; }
        public Guid? ReplyId { get; set; }
        public Action? OnSend { get; set; }
        public Func<PeerCredential, CancellationToken, Task>? BeforeHandshake { get; set; }
        public int Handshakes { get; private set; }
        public int Sends { get; private set; }
        public int PoolCount { get; private set; }
        public int DisposedPools { get; private set; }
        public int OrdinarySends { get; private set; }
        public int ChainChecks { get; private set; }
        public DateTimeOffset? OverlapUntil { get; set; }
        public string? ChainDigest { get; set; } = "verified-chain";
        public X509ChainStatusFlags ChainStatus { get; set; }
        public List<Guid> Confirmed { get; } = [];
        public List<Guid?> HandshakeLocals { get; } = [];

        public Scenario(bool legacy = false)
        {
            this.legacy = legacy;
            DateTimeOffset now = Clock.Now;
            OldExpires = now.AddHours(1);
            oldLeaf = MakeLeaf(OldExpires);
            nextLeaf = MakeLeaf(now.AddHours(3));
            localLeaf = MakeLeaf(now.AddHours(3));
            nextLocalLeaf = MakeLeaf(now.AddHours(3));
            Old = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new("https://peer.example.test/internal/raft"),
                Fingerprint(oldLeaf), true, false, now.AddHours(2));
            Next = Old with { CredentialId = Guid.NewGuid(), Fingerprint = Fingerprint(nextLeaf) };
            Offered = Old;
            accepted = [Old, Next];
            ClusterOptions options = new() { NodeId = Guid.NewGuid(), StorageGeneration = Guid.NewGuid(),
                ClusterId = Guid.NewGuid(), Generation = 1, CredentialId = Guid.NewGuid() };
            state.Publish(new(Guid.NewGuid(), "Bootstrap", InstallationPhase.ClusterReady, 1, 1, 1, Guid.NewGuid(),
                now.AddHours(3), CredentialId: options.CredentialId, TrustAnchorFingerprint: Fingerprint(localLeaf)));
            tls = new(state, time: Clock);
            tls.Install(1, localLeaf, localLeaf, [], _ => false);
            HttpMessageHandler transport = legacy
                ? new LegacyPeerTransport(localLeaf, this, Clock, (cert, pooled, confirm) => CreateWire(cert,
                    options.CredentialId, pooled, (message, server, presented, token, chain) =>
                        confirm(message, server, presented, token, chain)), new OrdinaryWire(this))
                : new ManagedPeerTransport(tls, this, Clock, (snapshot, pooled, confirm) => CreateWire(snapshot.Certificate,
                    snapshot.CredentialId, pooled, confirm));
            client = new(new PeerClientHandler(Options.Create(options), this, Clock,
                new ClusterRuntimeState(new UnavailableClusterRecovery()), legacy ? null :
                    Options.Create(new InstallationOptions { InstallationIntent = "Setup" }), legacy ? null : tls)
            {
                InnerHandler = transport
            });
        }

        private Wire CreateWire(X509Certificate2 local, Guid? credentialId, bool pooled, ManagedPeerTransport.ConfirmConnection confirm)
        {
            if (pooled)
            {
                PoolCount++;
            }

            return new(this, local, credentialId, pooled, confirm);
        }

        public async Task SendAsync(bool typed = true, bool switchBeforeDispatch = false)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, Old.Address);
            if (typed)
            {
                DynamicPeerTls.Snapshot local = Assert.IsType<DynamicPeerTls.Snapshot>(tls.Capture());
                request.Options.Set(PeerClientHandler.CONNECTION_BINDING, new(Old.NodeId, Old.StorageGeneration,
                    Old.Address, local.Fingerprint, peer => Confirmed.Add(peer.CredentialId))
                    { ExpectedLocalSnapshot = legacy ? null : local });
            }

            if (switchBeforeDispatch)
            {
                NextLocalGeneration();
            }

            using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        public void NextLocalGeneration()
        {
            LocalInstallationProfile profile = state.Current!;
            state.Publish(profile with { ProfileRevision = 2, RuntimeGeneration = 2, CredentialId = Guid.NewGuid() });
            tls.Install(2, nextLocalLeaf, localLeaf, [], _ => false);
        }

        public void RetireOld()
        {
            accepted = [Next];
        }

        public void RetireNext()
        {
            accepted = [Old];
        }

        public ValueTask<IReadOnlyList<PeerCredential>> ListAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<PeerCredential>>(Clock.Now >= OverlapUntil
                ? accepted.Where(peer => peer != Old).ToArray() : accepted);
        }

        public async ValueTask<PeerCredential?> FindAsync(Guid credentialId, CancellationToken cancellationToken)
        {
            IReadOnlyList<PeerCredential> current = await ListAsync(cancellationToken);
            return current.SingleOrDefault(peer => peer.CredentialId == credentialId);
        }

        private X509Certificate2 MakeLeaf(DateTimeOffset expires)
        {
            using ECDsa key = ECDsa.Create();

            return new CertificateRequest("CN=peer.example.test", key, HashAlgorithmName.SHA256)
                .CreateSelfSigned(Clock.Now.AddMinutes(-1), expires);
        }

        private static string Fingerprint(X509Certificate2 cert)
        {
            return cert.GetCertHashString(HashAlgorithmName.SHA256);
        }

        public void Dispose()
        {
            client.Dispose();
            oldLeaf.Dispose();
            nextLeaf.Dispose();
            localLeaf.Dispose();
            nextLocalLeaf.Dispose();
        }

        private class OrdinaryWire(Scenario test) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Assert.Null(request.Headers.ConnectionClose);
                Assert.False(request.Options.TryGetValue(PeerClientHandler.PRESENTED_SERVER, out _));
                test.OrdinarySends++;
                PeerEnvelope sent = PeerHeaders.Decode(request.Headers.GetValues(PeerHeaders.ENVELOPE).Single())!;
                HttpResponseMessage response = new(HttpStatusCode.OK);
                response.Headers.Add(PeerHeaders.ENVELOPE, PeerHeaders.Encode(sent with
                {
                    FromNodeId = sent.ToNodeId, ToNodeId = sent.FromNodeId,
                    CredentialId = test.Offered.CredentialId, StorageGeneration = test.Offered.StorageGeneration
                }));
                return Task.FromResult(response);
            }
        }

        private class Wire(Scenario test, X509Certificate2 localCertificate, Guid? credentialId, bool pooled,
            ManagedPeerTransport.ConfirmConnection confirm) : HttpMessageHandler
        {
            private PeerCredential? connected;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                PeerCredential peer = test.Offered;
                Assert.Equal(!pooled, request.Headers.ConnectionClose);
                if (!pooled || connected is null || test.Reconnect)
                {
                    if (test.BeforeHandshake is { } before)
                    {
                        await before(peer, token);
                    }

                    test.Handshakes++;
                    test.HandshakeLocals.Add(credentialId);
                    X509Certificate2 offered = peer == test.Old ? test.oldLeaf : test.nextLeaf;
                    X509Certificate2 local = test.WrongLocal ? test.nextLocalLeaf : localCertificate;
                    PeerServerPin.ChainProof? chain = test.legacy && test.ChainDigest is { } digest
                        ? new(digest, _ =>
                        {
                            test.ChainChecks++;
                            return test.ChainStatus == X509ChainStatusFlags.NoError;
                        }) : null;
                    await confirm(request, offered, local, token, chain);
                    connected = peer;
                    test.Reconnect = false;
                }

                test.Sends++;
                if (test.OnSend is { } onSend)
                {
                    test.OnSend = null;
                    onSend();
                }

                PeerEnvelope sent = PeerHeaders.Decode(request.Headers.GetValues(PeerHeaders.ENVELOPE).Single())!;
                Assert.Equal(credentialId, sent.CredentialId);
                PeerEnvelope reply = sent with { FromNodeId = sent.ToNodeId, ToNodeId = sent.FromNodeId,
                    CredentialId = test.ReplyId ?? connected.CredentialId, StorageGeneration = connected.StorageGeneration };
                HttpResponseMessage response = new(HttpStatusCode.OK);
                response.Headers.Add(PeerHeaders.ENVELOPE, PeerHeaders.Encode(reply));
                return response;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && pooled)
                {
                    test.DisposedPools++;
                }

                base.Dispose(disposing);
            }
        }
    }

    internal class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }
}
