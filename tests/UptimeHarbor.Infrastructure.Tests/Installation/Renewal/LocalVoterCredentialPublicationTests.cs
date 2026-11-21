using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Installation.Renewal;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation.Renewal;

public class LocalVoterCredentialPublicationTests
{
    [Fact]
    public async Task RestartUsesQuarantineOnlyForRaftAndWorkingRevisionForOrdinaryPeerRpc()
    {
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        await fixture.BindAsync();
        Runtime runtime = new(fixture);
        await runtime.InstallAsync();
        var quarantine = runtime.PrepareRestart();

        DateTimeOffset now = fixture.Local.Authority.Time.Now;
        using ECDsa key = ECDsa.Create();
        using X509Certificate2 server = new CertificateRequest("CN=issuer.example.test", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(now.AddDays(-1), now.AddDays(2));
        RemoteTrust trust = new(server, now);
        List<DynamicPeerTls.Snapshot> transports = [];
        using HttpClient client = runtime.CreateClient(trust, (snapshot, pooled, confirm) =>
        {
            Assert.False(pooled);
            transports.Add(snapshot);
            return new RemoteHandler(trust, fixture.Local.Authority.Time, snapshot, null, new(), confirm);
        });
        Uri health = new(trust.Peer.Address, "/internal/cluster/v1/health");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(health, fixture.Token));
        Assert.Empty(transports);
        using HttpResponseMessage native = await client.GetAsync(trust.Peer.Address, fixture.Token);
        Assert.Equal(HttpStatusCode.OK, native.StatusCode);
        Assert.True(quarantine.Quarantined);

        Assert.True(await runtime.ConfirmRestartAsync());
        var working = Assert.IsType<DynamicPeerTls.Snapshot>(runtime.Tls.Capture());
        using HttpResponseMessage ordinary = await client.GetAsync(health, fixture.Token);
        Assert.Equal(HttpStatusCode.OK, ordinary.StatusCode);
        Assert.False(working.Quarantined);
        Assert.Equal(fixture.Result.CredentialRevision, working.CredentialRevision);
        Assert.Equal(quarantine.CredentialId, working.CredentialId);
        Assert.Equal([quarantine, working], transports);
        Assert.False(runtime.Tls.IsUsable(quarantine));
    }

    [Fact]
    public async Task RestartPublicationRacingAnApplyRequiresFreshRetry()
    {
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        await fixture.BindAsync();
        Runtime runtime = new(fixture);
        await runtime.InstallAsync();
        var quarantine = runtime.PrepareRestart();
        var exact = runtime.Store.Current!;
        var advanced = exact with { Index = exact.Index + 1, Digest = new string('A', 64) };
        bool apply = true;
        using (RuntimeGenerationContext context = new(() =>
        {
            if (apply)
            {
                apply = false;
                runtime.Store.Publish(advanced);
            }
        }))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(runtime.ConfirmRestartAsync);
        }

        Assert.Same(quarantine, runtime.Tls.Capture());
        Assert.True(await runtime.ConfirmRestartAsync());
        Assert.False(runtime.Tls.Capture()!.Quarantined);
        Assert.Single(fixture.Transport.Sent);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("fork")]
    public async Task RestartRejectsCredentialOrCutInvalidatedAfterReplay(string failure)
    {
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        await fixture.BindAsync();
        Runtime runtime = new(fixture);
        await runtime.InstallAsync();
        var quarantine = runtime.PrepareRestart();
        var image = runtime.Store.Current!;
        var credential = image.Operations.Credentials[quarantine.CredentialId!.Value];
        switch (failure)
        {
            case "expired":
                fixture.Local.Authority.Time.Now = credential.ExpiresAtUtc;
                break;
            case "revoked":
                runtime.Store.Publish(image with { Index = image.Index + 1, Operations = image.Operations with
                {
                    Credentials = image.Operations.Credentials.SetItem(credential.Id,
                        credential with { RevokedAtUtc = fixture.Local.Authority.Time.Now })
                } });
                break;
            default:
                runtime.Store.Publish(image with { Digest = new string('F', 64) });
                break;
        }

        Assert.False(await runtime.ConfirmRestartAsync());
        Assert.Null(runtime.Tls.Capture());
        Assert.Single(fixture.Transport.Sent);
    }

    [Fact]
    public async Task VerifiedInstallation_SwitchesOnceAndReopensWithNewLeaseAndAcceptedStore()
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        await fixture.BindAsync();
        Runtime runtime = new(fixture);
        await runtime.InstallAsync();
        DynamicPeerTls.Snapshot previous = runtime.InstallBaseline();

        // Act
        DynamicPeerTls.Snapshot published = await runtime.PublishAsync();
        DynamicPeerTls.Snapshot replay = await runtime.PublishAsync();

        // Assert
        Assert.Same(published, replay);
        Assert.Same(published.Certificate, runtime.Tls.Certificate);
        Assert.Equal(fixture.Result.CredentialRevision, published.CredentialRevision);
        Assert.Equal(fixture.Local.Artifact!.IssueId, published.CredentialId);
        Assert.Equal(fixture.Local.Artifact.CertificateDigest, published.Fingerprint);
        Assert.True(published.Certificate.HasPrivateKey);
        Assert.True(runtime.Tls.IsUsable(previous));
        Assert.False(fixture.Local.State.CommonOpen);

        // Arrange
        RevisionStateStore store = new();
        store.Publish(fixture.Local.Authority.Scenario.State);
        fixture.Reopen();
        Runtime reopened = new(fixture, store);
        Assert.Null(reopened.Tls.Certificate);

        // Act
        DynamicPeerTls.Snapshot recovered = await reopened.PublishAsync();

        // Assert
        Assert.Equal(published.CredentialRevision, recovered.CredentialRevision);
        Assert.Equal(published.Fingerprint, recovered.Fingerprint);
        Assert.True(recovered.Certificate.HasPrivateKey);
        Assert.Empty(fixture.Local.Files.Writes);
        Assert.Single(fixture.Transport.Sent);
        Assert.Equal(1, fixture.Local.Authority.Remote.Calls);
        Assert.False(fixture.Local.State.CommonOpen);
    }

    [Theory]
    [InlineData("install-confirmation", false)]
    [InlineData("install-reference", true)]
    [InlineData("intent", true)]
    [InlineData("bind-result", false)]
    public async Task PartialOrCorruptEvidence_LatchesClosedUntilExactRecovery(string stage, bool corrupt)
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        await fixture.BindAsync();
        Runtime runtime = new(fixture);
        await runtime.InstallAsync();
        DynamicPeerTls.Snapshot old = runtime.InstallBaseline();
        string path = fixture.Local.PathFor(stage);
        byte[] original = File.ReadAllBytes(path);
        if (corrupt)
        {
            File.WriteAllText(path, "invalid envelope");
        }
        else
        {
            File.Delete(path);
        }

        // Act
        Exception? error = await Record.ExceptionAsync(runtime.PublishAsync);

        // Assert
        Assert.NotNull(error);
        Assert.Null(runtime.Tls.Certificate);
        Assert.False(runtime.Tls.IsUsable(old));

        // Arrange
        File.WriteAllBytes(path, original);

        // Act
        DynamicPeerTls.Snapshot published = await runtime.PublishAsync();

        // Assert
        Assert.Same(published, runtime.Tls.Capture());
        Assert.False(runtime.Tls.IsUsable(old));
        Assert.Single(fixture.Transport.Sent);
    }

    [Theory]
    [InlineData("fork")]
    [InlineData("rollback")]
    [InlineData("recovery")]
    [InlineData("generation")]
    public async Task AcceptedCutOrGenerationChangesAtCommit_RejectsPublication(string failure)
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        RevisionImage authorized = fixture.Local.Authority.Scenario.State;
        await fixture.BindAsync();
        Runtime runtime = new(fixture);
        await runtime.InstallAsync();
        _ = runtime.InstallBaseline();
        RevisionImage exact = fixture.Local.Authority.Scenario.State;
        bool triggered = false;

        // Act
        using (RuntimeGenerationContext context = new(() =>
        {
            if (triggered)
            {
                return;
            }

            triggered = true;
            switch (failure)
            {
                case "fork":
                    runtime.Store.Publish(exact with { Digest = new string('F', 64) });
                    break;
                case "rollback":
                    runtime.Store.Publish(authorized);
                    break;
                case "recovery":
                    runtime.Store.Invalidate();
                    break;
                default:
                    throw new InvalidOperationException("SimulatedGenerationDrain");
            }
        }))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(runtime.PublishAsync);
        }

        // Assert
        Assert.True(triggered);
        Assert.Null(runtime.Tls.Capture());

        // Act
        Assert.True(runtime.Store.ConfirmRecovery(exact, runtime.Store.RecoveryVersion));
        DynamicPeerTls.Snapshot recovered = await runtime.PublishAsync();

        // Assert
        Assert.Equal(fixture.Result.CredentialRevision, recovered.CredentialRevision);
        Assert.Same(recovered, runtime.Tls.Capture());
    }

    [Fact]
    public async Task ObservedRollbackAfterSwitch_RequiresFreshPublicationEvenAfterAcceptedCutReturns()
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        RevisionImage authorized = fixture.Local.Authority.Scenario.State;
        await fixture.BindAsync();
        Runtime runtime = new(fixture);
        await runtime.InstallAsync();
        DynamicPeerTls.Snapshot original = await runtime.PublishAsync();
        RevisionImage exact = fixture.Local.Authority.Scenario.State;

        // Act
        runtime.Store.Publish(authorized);
        DynamicPeerTls.Snapshot? stale = runtime.Tls.Capture();
        runtime.Store.Publish(exact);
        DynamicPeerTls.Snapshot? beforeRecovery = runtime.Tls.Capture();
        DynamicPeerTls.Snapshot recovered = await runtime.PublishAsync();

        // Assert
        Assert.Null(stale);
        Assert.Null(beforeRecovery);
        Assert.False(runtime.Tls.IsUsable(original));
        Assert.NotSame(original, recovered);
        Assert.Equal(original.CredentialRevision, recovered.CredentialRevision);
        Assert.Same(recovered, runtime.Tls.Capture());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InFlightEnvelopeAndTransportKeepSnapshot_UntilOverlapEnds(bool expire)
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        await fixture.BindAsync();
        Runtime runtime = new(fixture);
        await runtime.InstallAsync();
        DynamicPeerTls.Snapshot old = runtime.InstallBaseline();
        using ECDsa key = ECDsa.Create();
        DateTimeOffset now = fixture.Local.Authority.Time.Now;
        using X509Certificate2 server = new CertificateRequest("CN=issuer.example.test", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(now.AddDays(-1), now.AddDays(2));
        RemoteTrust trust = new(server, now);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<DynamicPeerTls.Snapshot> transports = [];
        using HttpClient client = runtime.CreateClient(trust, (snapshot, pooled, confirm) =>
        {
            Assert.False(pooled);
            transports.Add(snapshot);
            return new RemoteHandler(trust, fixture.Local.Authority.Time, snapshot,
                ReferenceEquals(snapshot, old) ? entered : null, release, confirm);
        });

        // Act
        Task<HttpResponseMessage> pending = client.GetAsync(trust.Peer.Address, fixture.Token);
        Task first = await Task.WhenAny(entered.Task, pending).WaitAsync(fixture.Token);
        Assert.Same(entered.Task, first);
        DynamicPeerTls.Snapshot next = await runtime.PublishAsync();
        if (expire)
        {
            fixture.Local.Authority.Time.Now = fixture.Result.OverlapUntilUtc;
        }

        using HttpResponseMessage fresh = await client.GetAsync(trust.Peer.Address, fixture.Token);
        release.SetResult();
        Exception? failure = await Record.ExceptionAsync(async () =>
        {
            using HttpResponseMessage response = await pending;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        });

        // Assert
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Equal([old, next], transports);
        Assert.Equal(!expire, runtime.Tls.IsUsable(old));
        if (expire)
        {
            Assert.IsType<HttpRequestException>(failure);
        }
        else
        {
            Assert.Null(failure);
        }
    }

    private class Runtime
    {
        private readonly LocalVoterCredentialBindingFixture fixture;
        private readonly LocalVoterCredentialInstallationCoordinator installed;
        private readonly LocalVoterCredentialPublication publisher;
        public DynamicPeerTls Tls { get; }
        public RevisionStateStore Store { get; }

        public Runtime(LocalVoterCredentialBindingFixture fixture, RevisionStateStore? store = null)
        {
            this.fixture = fixture;
            var authority = fixture.Local.Authority;
            Store = store ?? authority.Subject;
            var renewed = new VoterCredentialRenewalAuthority(Store, authority.SubjectBarrier, new(authority.Time),
                Options.Create(authority.SubjectOptions), authority.Time);
            LocalVoterCredentialBindingCoordinator binding = new(fixture.Local.Store, fixture.Local.Files, fixture.Local.State,
                renewed, fixture.Transport, authority.Time, fixture.Logs);
            installed = new(binding, fixture.Local.Files, fixture.Local.State,
                NullLogger<LocalVoterCredentialInstallationCoordinator>.Instance);
            Tls = new(fixture.Local.State, Store, authority.Time);
            publisher = new(installed, fixture.Coordinator, fixture.Local.Files, fixture.Local.Lease, fixture.Local.State, Store, Tls);

            var request = authority.Request;
            LocalInstallationProfile profile = new(fixture.Local.Lease.InstallIdentity, "Bootstrap",
                InstallationPhase.ClusterReady, 4, 3, 1, fixture.Local.Lease.BootId, authority.Time.Now.AddHours(1),
                NodeId: authority.SubjectId, StorageGeneration: authority.SubjectStorage,
                PeerAddress: request.Renewal.Issuance.PeerUri, ClusterId: request.ClusterId,
                ClusterGeneration: request.Generation, CredentialId: request.Renewal.PreviousCredentialId,
                TrustAnchorFingerprint: EnrollmentCertificates.Hash(authority.Scenario.Crypto.Issuer.RawData));
            fixture.Local.State.Publish(profile);
        }

        public DynamicPeerTls.Snapshot InstallBaseline()
        {
            var scenario = fixture.Local.Authority.Scenario;
            X509Certificate2 certificate = scenario.Previous.CopyWithPrivateKey(scenario.Crypto.PeerKey);
            Tls.Install(fixture.Local.State.Current!.RuntimeGeneration, certificate, scenario.Crypto.Issuer,
                [scenario.Intent.PreviousFingerprint], _ => false, scenario.Crypto.Epoch);
            return Assert.IsType<DynamicPeerTls.Snapshot>(Tls.Capture());
        }

        public Task InstallAsync()
        {
            return installed.InstallAsync(fixture.Local.Reference, fixture.Token);
        }

        public Task<DynamicPeerTls.Snapshot> PublishAsync()
        {
            return publisher.PublishAsync(fixture.Local.Reference, fixture.Token);
        }

        public DynamicPeerTls.Snapshot PrepareRestart()
        {
            var previous = fixture.Local.State.Current!;
            var profile = previous with
            {
                ManagedRestart = true, Phase = InstallationPhase.ClusterQuarantine,
                ProfileRevision = previous.ProfileRevision + 1
            };
            fixture.Local.State.Publish(profile);
            RevisionImage image = Store.Current!;
            publisher.PrepareQuarantine(profile, image, VoterCredentialPolicy.Latest(image, profile.NodeId!.Value)!,
                Store.RecoveryVersion);
            return Assert.IsType<DynamicPeerTls.Snapshot>(Tls.Capture());
        }

        public Task<bool> ConfirmRestartAsync()
        {
            return publisher.ConfirmRestartAsync(fixture.Token);
        }

        public HttpClient CreateClient(RemoteTrust trust, ManagedPeerTransport.HandlerFactory handler)
        {
            var authority = fixture.Local.Authority;
            PeerClientHandler envelope = new(Options.Create(authority.SubjectOptions), trust, authority.Time,
                new ClusterRuntimeState(new UnavailableClusterRecovery()),
                Options.Create(new InstallationOptions { InstallationIntent = "Setup" }), Tls)
            {
                InnerHandler = new ManagedPeerTransport(Tls, trust, authority.Time, handler)
            };

            return new(envelope);
        }
    }

    private class RemoteTrust(X509Certificate2 certificate, DateTimeOffset now) : IPeerTrustSource
    {
        public PeerCredential Peer { get; } = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new Uri("https://issuer.example.test/internal/raft"), EnrollmentCertificates.Hash(certificate.RawData),
            true, false, now.AddDays(2));
        public X509Certificate2 Certificate { get; } = certificate;

        public ValueTask<PeerCredential?> FindAsync(Guid id, CancellationToken cancellationToken)
        {
            PeerCredential? found = null;
            if (id == Peer.CredentialId)
            {
                found = Peer;
            }

            return ValueTask.FromResult(found);
        }

        public ValueTask<IReadOnlyList<PeerCredential>> ListAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<PeerCredential>>([Peer]);
        }
    }

    private class RemoteHandler(RemoteTrust trust, TimeProvider time, DynamicPeerTls.Snapshot snapshot,
        TaskCompletionSource? entered, TaskCompletionSource release, ManagedPeerTransport.ConfirmConnection confirm)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            PeerEnvelope sent = PeerHeaders.Decode(request.Headers.GetValues(PeerHeaders.ENVELOPE).Single())!;
            Assert.Equal(snapshot.CredentialId, sent.CredentialId);
            await confirm(request, trust.Certificate, snapshot.Certificate, cancellationToken);
            if (entered is not null)
            {
                entered.SetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            PeerEnvelope reply = sent with { FromNodeId = sent.ToNodeId, ToNodeId = sent.FromNodeId,
                CredentialId = trust.Peer.CredentialId, StorageGeneration = trust.Peer.StorageGeneration, IssuedAtUtc = time.GetUtcNow() };
            HttpResponseMessage response = new(HttpStatusCode.OK);
            response.Headers.Add(PeerHeaders.ENVELOPE, PeerHeaders.Encode(reply));
            return response;
        }
    }
}
