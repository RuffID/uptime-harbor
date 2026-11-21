using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Installation.Renewal;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation.Renewal;

public class LocalVoterContinuationPublicationTests
{
    [Fact]
    public async Task AcceptedProcessHandoffRequiresFreshPublicationBeforeEnable()
    {
        // Arrange
        using LocalVoterContinuationInstallationFixture fixture = new();
        CommandResult bind = await InstallAsync(fixture);
        Runtime runtime = new(fixture);
        OnlineGrant process = runtime.Grant(OnlineGrantPurpose.Process, bind.Digest);
        OnlineGrant enable = runtime.Grant(OnlineGrantPurpose.Enable);
        DateTimeOffset now = fixture.Scenario.Time.Now;
        using ECDsa remoteKey = ECDsa.Create();
        using X509Certificate2 remote = new CertificateRequest("CN=peer.invalid", remoteKey, HashAlgorithmName.SHA256)
            .CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
        List<DynamicPeerTls.Snapshot> selected = [];
        using HttpClient client = runtime.CreatePeerClient(remote, selected);
        Uri ordinaryPeer = new("https://peer.invalid/internal/cluster/v1/health");
        Uri nativePeer = new("https://peer.invalid/internal/raft");

        // Act
        var quarantine = await runtime.PublishAsync();
        OnlineGrant signed = await runtime.Signer.SignAsync(process, fixture.Token);
        bool acceptedEnableInQuarantine = runtime.Enable.Confirm(enable);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(ordinaryPeer, fixture.Token));
        using HttpResponseMessage native = await client.GetAsync(nativePeer, fixture.Token);

        // Assert
        Assert.Same(quarantine, await runtime.PublishAsync());
        Assert.True(quarantine.Quarantined);
        Assert.Equal(HttpStatusCode.OK, native.StatusCode);
        Assert.Same(quarantine, Assert.Single(selected));
        Assert.Equal(fixture.Artifact.CertificateDer, quarantine.Certificate.RawData);
        Assert.True(quarantine.Certificate.HasPrivateKey);
        Assert.Equal(bind.Index, quarantine.CredentialRevision);
        Assert.Null(runtime.Tls.CaptureWorkingBootstrap(fixture.Scenario.State, runtime.Profile));
        Assert.True(OnlineGrantSignatures.Verify(fixture.Scenario.State, signed, fixture.Scenario.Time.Now, processRenewal: true));
        Assert.False(acceptedEnableInQuarantine);

        // Act
        CommandResult renewed = await runtime.ApplyAsync("RenewOnlineProcess", signed);
        enable = runtime.SignEnable(runtime.Grant(OnlineGrantPurpose.Enable));
        CommandResult authorized = await runtime.ApplyAsync("AuthorizeOnlineEnable", enable);
        CommandResult consumed = await runtime.ApplyAsync("EnableOnlineGrant", enable);

        // Assert
        Assert.Equal(OperationOutcome.Committed, renewed.Outcome);
        Assert.Equal(OperationOutcome.Committed, authorized.Outcome);
        Assert.Equal(OperationOutcome.Committed, consumed.Outcome);
        Assert.Same(quarantine, runtime.Tls.Capture());
        Assert.False(runtime.Enable.Confirm(enable));
        Assert.False(fixture.State.CommonOpen);

        // Act
        var working = await runtime.PublishAsync();
        bool enabled = runtime.Enable.Confirm(enable);
        using HttpResponseMessage ordinaryRpc = await client.GetAsync(ordinaryPeer, fixture.Token);
        OnlineGrant ordinary = await runtime.Signer.SignAsync(runtime.Grant(OnlineGrantPurpose.Process), fixture.Token);
        CommandResult ordinaryResult = await runtime.ApplyAsync("RenewOnlineProcess", ordinary);
        var recovered = await fixture.Service.RecoverAsync(fixture.Reference, fixture.Token);

        // Assert
        Assert.NotSame(quarantine, working);
        Assert.False(runtime.Tls.IsUsable(quarantine));
        Assert.False(working.Quarantined);
        Assert.Equal(HttpStatusCode.OK, ordinaryRpc.StatusCode);
        Assert.Equal([quarantine, working], selected);
        Assert.Same(working, await runtime.PublishAsync());
        Assert.True(enabled);
        Assert.True(runtime.Enable.Allows());
        Assert.Equal(consumed.Index, runtime.Enable.AcceptedIndex);
        Assert.False(fixture.State.CommonOpen);
        Assert.Equal(working.CredentialId, ordinary.CredentialId);
        Assert.Equal(OperationOutcome.Committed, ordinaryResult.Outcome);
        Assert.Equal(fixture.Artifact.IssueId, recovered.IssueId);
        Assert.Equal(bind.Index, recovered.CredentialRevision);
        Assert.Equal(bind, fixture.AcceptedBind());
        Assert.Equal(1, fixture.Signing.Journal.Commits);
        Assert.Equal(8, fixture.Scenario.Wal.Count);
    }

    [Theory]
    [InlineData("bind")]
    [InlineData("key")]
    [InlineData("recovery")]
    [InlineData("conflict")]
    [InlineData("expired")]
    public async Task ExactLiveProcessEvidenceIsRequiredForHandoff(string change)
    {
        // Arrange
        using LocalVoterContinuationInstallationFixture fixture = new();
        CommandResult bind = await InstallAsync(fixture);
        Runtime runtime = new(fixture);
        OnlineGrant request = runtime.Grant(OnlineGrantPurpose.Process, bind.Digest);
        await runtime.PublishAsync();
        OnlineProcessLease original = fixture.Scenario.State.OnboardingContracts!.OnlineProcesses![request.NodeId];
        switch (change)
        {
            case "bind":
                request = request with { SnapshotDigest = new string('F', 64) };
                break;
            case "key":
                request = request with { SpkiDigest = new string('F', 64) };
                break;
            case "recovery":
                request = request with { RecoveryVersion = request.RecoveryVersion + 1 };
                break;
            case "conflict":
                request = request with { BootId = Guid.NewGuid(), ProcessEpoch = request.ProcessEpoch + 1 };
                break;
            default:
                fixture.Scenario.Time.Now = original.LeaseUntilUtc;
                request = request with { IssuedAtUtc = original.LeaseUntilUtc, ExpiresAtUtc = original.LeaseUntilUtc.AddMinutes(2) };
                break;
        }

        byte[] signature = fixture.Scenario.Crypto.NewKey.SignData(OnlineLifecycleCommands.Canonical(request, processRenewal: true),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        OnlineGrant signed = request with { Signature = Convert.ToBase64String(signature) };

        // Act
        var decision = await runtime.ApplyAsync("RenewOnlineProcess", signed);
        var replaced = await runtime.ApplyAsync("RegisterOnlineProcess", signed with { GrantId = Guid.NewGuid() });

        // Assert
        Assert.Equal(OperationOutcome.Rejected, decision.Outcome);
        Assert.Equal(OperationOutcome.Rejected, replaced.Outcome);
        Assert.Equal(original, fixture.Scenario.State.OnboardingContracts!.OnlineProcesses![request.NodeId]);
        Assert.False(runtime.Enable.Allows());
        Assert.False(fixture.State.CommonOpen);
    }

    [Theory]
    [InlineData("restart", typeof(UnauthorizedAccessException))]
    [InlineData("restart-expired", typeof(UnauthorizedAccessException))]
    [InlineData("majority", typeof(InvalidOperationException))]
    [InlineData("revoked", typeof(InvalidDataException))]
    [InlineData("fork", typeof(InvalidDataException))]
    public async Task PublicationFailsClosedAndLocalBindRecoveryPreservesIdentity(string change, Type refusal)
    {
        // Arrange
        using LocalVoterContinuationInstallationFixture fixture = new();
        CommandResult bind = await InstallAsync(fixture);
        Runtime runtime = new(fixture);
        var previous = await runtime.PublishAsync();
        RevisionImage exact = fixture.Scenario.State;
        DateTimeOffset now = fixture.Scenario.Time.Now;
        if (change.StartsWith("restart", StringComparison.Ordinal))
        {
            fixture.Reopen();
            runtime = new(fixture);
        }

        switch (change)
        {
            case "restart-expired":
                fixture.Scenario.Time.Now = exact.OnboardingContracts!.OnlineProcesses![runtime.Profile.NodeId!.Value].LeaseUntilUtc;
                break;
            case "majority":
                fixture.Scenario.Barrier.Available = false;
                break;
            case "revoked":
                OperationalCredential leaf = exact.Operations.Credentials[fixture.Artifact.IssueId];
                fixture.Scenario.Store.Publish(exact with { Operations = exact.Operations with
                {
                    Credentials = exact.Operations.Credentials.SetItem(leaf.Id, leaf with { RevokedAtUtc = now })
                } });
                Assert.False(runtime.Tls.IsUsable(previous));
                break;
            case "fork":
                fixture.Scenario.Store.Publish(exact with { Digest = new string('F', 64) });
                Assert.False(runtime.Tls.IsUsable(previous));
                break;
        }

        // Act
        await Assert.ThrowsAsync(refusal, () => runtime.PublishAsync());

        // Assert
        Assert.Null(runtime.Tls.Capture());
        Assert.False(runtime.Enable.Allows());
        Assert.False(fixture.State.CommonOpen);

        // Act
        fixture.Scenario.Barrier.Available = true;
        fixture.Scenario.Store.Publish(exact);
        var recovered = await fixture.Service.RecoverAsync(fixture.Reference, fixture.Token);

        // Assert
        Assert.Equal(fixture.Artifact.IssueId, recovered.IssueId);
        Assert.Equal(bind.Index, recovered.CredentialRevision);
        Assert.Equal(bind, fixture.AcceptedBind());
        Assert.Equal(1, fixture.Signing.Journal.Commits);
        Assert.Equal(4, fixture.Scenario.Wal.Count);
    }

    [Fact]
    public async Task ProjectionChangeAtAtomicSwapRequiresFreshRetry()
    {
        // Arrange
        using LocalVoterContinuationInstallationFixture fixture = new();
        CommandResult bind = await InstallAsync(fixture);
        Runtime runtime = new(fixture);
        RevisionImage image = fixture.Scenario.State;
        bool advance = true;
        using RuntimeGenerationContext context = new(() =>
        {
            if (advance)
            {
                advance = false;
                fixture.Scenario.Store.Publish(image with { Index = image.Index + 1, Digest = new string('A', 64) });
            }
        });

        // Act
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.PublishAsync());

        // Assert
        Assert.Equal("PeerCredentialAcceptedCutChanged", refusal.Message);
        Assert.Null(runtime.Tls.Capture());

        // Act
        var published = await runtime.PublishAsync();

        // Assert
        Assert.True(published.Quarantined);
        Assert.Equal(fixture.Artifact.IssueId, published.CredentialId);
        Assert.Equal(bind.Index, published.CredentialRevision);
        Assert.Equal(1, fixture.Signing.Journal.Commits);
    }

    private static async Task<CommandResult> InstallAsync(LocalVoterContinuationInstallationFixture fixture)
    {
        await fixture.StageAsync();
        CommandResult bind = await fixture.BindAsync();
        await fixture.Service.InstallAsync(fixture.Reference, fixture.Receipt(bind), fixture.Token);
        return bind;
    }

    private sealed class Runtime
    {
        private readonly LocalVoterContinuationInstallationFixture fixture;
        private readonly LocalVoterContinuationPublication publication;
        public DynamicPeerTls Tls { get; }
        public OnlineEnableState Enable { get; }
        public OnlineProcessRenewalSigner Signer { get; }
        public LocalInstallationProfile Profile { get; }

        public Runtime(LocalVoterContinuationInstallationFixture fixture)
        {
            this.fixture = fixture;
            var scenario = fixture.Scenario;
            EnrollmentIssuance leaf = scenario.Request.Renewal.Issuance;
            AcceptedEnrollmentIssuer issuer = scenario.State.OnboardingContracts!.Issuers![leaf.IssuerEpoch];
            GenesisAuthorization genesis = scenario.State.OnboardingContracts.Genesis
                ?? new(1, Guid.NewGuid(), fixture.Lease.InstallIdentity, scenario.State.ClusterId, scenario.State.Generation,
                    leaf.NodeId, leaf.StorageGeneration, leaf.PeerUri, issuer.SpkiDigest, issuer.CertificateDigest, new string('D', 64), null);
            scenario.Store.Publish(scenario.State with { OnboardingContracts = scenario.State.OnboardingContracts with
            {
                Genesis = genesis, GenesisInitialized = true
            } });
            Profile = new(fixture.Lease.InstallIdentity, "Bootstrap", InstallationPhase.AwaitingActivation, 1, 1, 1,
                fixture.Lease.BootId, scenario.Time.Now.AddMinutes(2), AttemptId: genesis.GenesisId, NodeId: leaf.NodeId,
                StorageGeneration: leaf.StorageGeneration, PeerAddress: leaf.PeerUri, KeyReference: fixture.PreviousKeyReference,
                ClusterId: scenario.State.ClusterId, ClusterGeneration: scenario.State.Generation,
                CredentialId: scenario.Request.Renewal.PreviousCredentialId) { ManagedRestart = true };
            fixture.State.Publish(Profile);
            fixture.Local.Store.Profile = Profile;
            Tls = new(fixture.State, scenario.Store, scenario.Time);
            Enable = new(scenario.Store, fixture.State, fixture.Lease, new(scenario.Time), scenario.Time, Tls);
            Signer = new(fixture.Local.Store, fixture.State, fixture.Lease, scenario.Store, fixture.Local.Secrets, scenario.Time, Tls);
            publication = new(fixture.Service, fixture.State, fixture.Lease, scenario.Store, Tls,
                Options.Create(new ClusterOptions { TlsBoundary = "Direct" }), scenario.Time);
        }

        public Task<DynamicPeerTls.Snapshot> PublishAsync()
        {
            return publication.PublishAsync(fixture.Reference, fixture.Token);
        }

        public OnlineGrant Grant(OnlineGrantPurpose purpose, string? bindDigest = null)
        {
            var scenario = fixture.Scenario;
            RevisionImage image = scenario.State;
            EnrollmentIssuance leaf = scenario.Request.Renewal.Issuance;
            OnlineProcessLease lease = image.OnboardingContracts!.OnlineProcesses![leaf.NodeId];
            DateTimeOffset now = scenario.Time.Now;
            return new(1, purpose, image.ClusterId, image.Generation, Profile.AttemptId!.Value, null, null, leaf.NodeId,
                leaf.StorageGeneration, leaf.PeerUri, leaf.SpkiDigest, leaf.IssueId, fixture.Artifact.CertificateDigest,
                leaf.IssuerEpoch, Guid.NewGuid(), image.OnboardingContracts.Genesis!.AccountOrImportDigest,
                lease.BootId, lease.BootChallenge, lease.ProcessEpoch, scenario.Actor, image.Values[scenario.Actor].Revision, 0,
                lease.RecoveryVersion, image.Operations.Revision, image.Operations.MembershipVersion, image.Index, bindDigest,
                now, now.AddMinutes(2), string.Empty);
        }

        public OnlineGrant SignEnable(OnlineGrant grant)
        {
            byte[] signed = fixture.Scenario.Crypto.Crypto.IssuerKey.SignData(OnlineLifecycleCommands.Canonical(grant),
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            return grant with { Signature = Convert.ToBase64String(signed) };
        }

        public HttpClient CreatePeerClient(X509Certificate2 remote, List<DynamicPeerTls.Snapshot> selected)
        {
            var scenario = fixture.Scenario;
            PeerCredential peer = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new("https://peer.invalid/internal/raft"),
                EnrollmentCertificates.Hash(remote.RawData), true, false, scenario.Time.Now.AddHours(1));
            RemoteTrust trust = new(peer);
            ClusterOptions options = new()
            {
                ClusterId = scenario.State.ClusterId, Generation = scenario.State.Generation,
                NodeId = Profile.NodeId!.Value, StorageGeneration = Profile.StorageGeneration!.Value
            };
            PeerClientHandler client = new(Options.Create(options), trust, scenario.Time,
                new ClusterRuntimeState(new UnavailableClusterRecovery()),
                Options.Create(new InstallationOptions { InstallationIntent = "Setup" }), Tls);
            client.InnerHandler = new ManagedPeerTransport(Tls, trust, scenario.Time, (snapshot, _, confirm) =>
            {
                selected.Add(snapshot);
                return new PeerWire(peer, remote, snapshot, confirm);
            });
            return new(client);
        }

        public async Task<CommandResult> ApplyAsync(string action, OnlineGrant grant)
        {
            var scenario = fixture.Scenario;
            Guid operation = grant.GrantId;
            if (action == "EnableOnlineGrant")
            {
                operation = OnlineLifecycleCommands.ConsumeId(operation);
            }

            RevisionCommand command = new()
            {
                Schema = 3, ClusterId = grant.ClusterId, ClusterGeneration = grant.Generation, OperationId = operation,
                EntityId = grant.NodeId, EntityKind = DeletedEntityKind.Node, BaseRevision = grant.OperationsRevision,
                Action = action, SystemSource = "OnlineLifecycle", OriginNodeId = grant.NodeId,
                ActorUserId = Guid.Empty, ActorRevision = 0,
                CreatedAtUtc = scenario.Time.Now, OnlineLifecycle = new(1, grant.StorageGeneration, grant)
                {
                    CoordinatorAtUtc = scenario.Time.Now, CoordinatorTerm = scenario.Term
                }
            };
            await scenario.ReplicateAsync(RevisionCodec.Encode(command), RevisionCodec.COMMAND_ID, fixture.Token);
            return scenario.State.Results[operation];
        }
    }

    private sealed class RemoteTrust(PeerCredential peer) : IPeerTrustSource
    {
        public ValueTask<IReadOnlyList<PeerCredential>> ListAsync(CancellationToken token)
        {
            return ValueTask.FromResult<IReadOnlyList<PeerCredential>>([peer]);
        }

        public ValueTask<PeerCredential?> FindAsync(Guid credentialId, CancellationToken token)
        {
            Assert.Equal(peer.CredentialId, credentialId);
            return ValueTask.FromResult<PeerCredential?>(peer);
        }
    }

    private sealed class PeerWire(PeerCredential peer, X509Certificate2 remote, DynamicPeerTls.Snapshot local,
        ManagedPeerTransport.ConfirmConnection confirm) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            await confirm(request, remote, local.Certificate, token);
            PeerEnvelope sent = PeerHeaders.Decode(request.Headers.GetValues(PeerHeaders.ENVELOPE).Single())!;
            Assert.Equal(local.CredentialId, sent.CredentialId);
            HttpResponseMessage response = new(HttpStatusCode.OK);
            response.Headers.Add(PeerHeaders.ENVELOPE, PeerHeaders.Encode(sent with
            {
                FromNodeId = peer.NodeId, ToNodeId = sent.FromNodeId, StorageGeneration = peer.StorageGeneration,
                CredentialId = peer.CredentialId
            }));
            return response;
        }
    }
}
