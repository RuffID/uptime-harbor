using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Issuers;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Web.Cluster;
using UptimeHarbor.Web.Cluster.EnrollmentRouting;

namespace UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

/// <summary>Синтетический accepted voter; настоящий typed pipeline с памятью вместо SQL/native majority.</summary>
public class EnrollmentRoutingNode : IDisposable
{
    public ClusterOptions Options { get; }
    public InstallationOptions Local { get; }
    public InstallationLease Lease { get; }
    public InstallationState Installation { get; }
    public LocalSecretStore Secrets { get; }
    public ManagedBootstrapStore Store { get; }
    public X509Certificate2 Certificate { get; }
    public EnrollmentRoutingNative Native { get; }
    public ClusterRaftHandle Handle { get; } = new();
    public EnrollmentRoutingNative.Recovery Recovery { get; }
    public ClusterRuntimeState View { get; }
    public NodeReadiness Readiness { get; }
    public EnrollmentReadBarrier Barrier { get; }
    public EnrollmentPeerClient Client { get; }
    public EnrollmentPeerService Service { get; }
    public EnrollmentGateway Gateway { get; }
    public EnrollmentDescriptors Descriptors { get; }
    public EnrollmentNonces Nonces { get; }
    public EnrollmentCommandReceiver Receiver { get; }
    public EnrollmentIssuanceRouter Router { get; }
    public RevisionDispatcher Dispatcher { get; }
    public ClusterPeerRuntime Runtime { get; }
    public DynamicPeerTls Tls { get; }
    public EnrollmentRateLimiter Limits { get; }
    public Issuers.IssuerRegistrationFixture.CapturedLog<EnrollmentPeerController> Log { get; } = new();
    private readonly ConsensusOnlyState nativeState = new();
    private readonly HttpClient client;
    private readonly ServiceProvider services = new ServiceCollection().BuildServiceProvider();
    private readonly SenderAuthorityGate sender;
    private readonly PeerReplayInbox inbox;
    private readonly bool ownsLease;

    public EnrollmentRoutingNode(EnrollmentRoutingFixture fixture, ClusterOptions options, InstallationOptions local,
        InstallationLease lease, InstallationState installation, LocalSecretStore secrets, ManagedBootstrapStore store,
        X509Certificate2 certificate, bool ownsLease)
    {
        Options = options;
        Local = local;
        Lease = lease;
        Installation = installation;
        Secrets = secrets;
        Store = store;
        Certificate = certificate;
        this.ownsLease = ownsLease;
        var configured = Microsoft.Extensions.Options.Options.Create(options);
        var configuredLocal = Microsoft.Extensions.Options.Options.Create(local);
        Recovery = new(fixture.State);
        View = new(Recovery);
        Readiness = new(fixture.First.Time);
        foreach (string component in new[] { "Database", "Projection", "Storage", "Clock" })
        {
            Readiness.Report(component, true, "Ready", TimeSpan.FromDays(1));
        }

        IRaftCluster raft = DispatchProxy.Create<IRaftCluster, EnrollmentRoutingNative>();
        Native = (EnrollmentRoutingNative)(object)raft;
        Native.Store = nativeState;
        ((DotNext.Net.Cluster.Consensus.Raft.IPersistentState)nativeState).UpdateTermAsync(1, true, CancellationToken.None).GetAwaiter().GetResult();
        typeof(ClusterRaftHandle).GetProperty(nameof(ClusterRaftHandle.Current))!.SetValue(Handle, raft);
        Barrier = new(Handle, View, fixture.State, Readiness, configured);
        EnrollmentAuthority authority = new();
        OnboardingAcceptance acceptance = new(Handle, View, Readiness, configured, authority, fixture.First.Time, fixture.State);
        Dispatcher = new(new Consensus(fixture, options.NodeId), fixture.State, View, time: fixture.First.Time, onboarding: acceptance);
        Receiver = new(authority, Dispatcher, fixture.State, View, Barrier, configured, configuredLocal, fixture.First.Time);
        EnrollmentIssuerStorage storage = new(configuredLocal, configured, lease, secrets);
        EnrollmentSigner signer = new(store, new(store, new()), new(), storage, fixture.First.Time,
            NullLogger<EnrollmentSigner>.Instance, new EnrollmentSigningAuthorization(Handle, View, fixture.State, Readiness, configured, fixture.First.Time));
        OnlineGrantSigningAuthorization grantAuthority = new(Handle, View, fixture.State, Readiness, configured, fixture.First.Time);
        OnlineGrantSigner grants = new(store, grantAuthority, new(), storage, fixture.First.Time);
        Service = new(Receiver, signer, grants, fixture.State, Barrier, configured, fixture.First.Time);
        AcceptedPeerTrustSource trust = new(fixture.State, fixture.First.Time);
        PeerClientHandler authenticated = new(configured, trust, fixture.First.Time, View)
        {
            InnerHandler = new EnrollmentRoutingHandler(fixture, this)
        };
        client = new(authenticated);
        Client = new(Handle, View, fixture.State, Barrier, Receiver, trust, new Clients(client), configured, fixture.First.Time);
        Router = new(signer, grants, fixture.State, Client, configured, fixture.First.Time);
        Descriptors = new(fixture.State, configuredLocal, configured, fixture.First.Time);
        Nonces = new(fixture.First.Time);
        Gateway = new(Descriptors, Nonces, new(), new(store, installation, fixture.First.Time), Client, Router,
            configured, configuredLocal, fixture.First.Time, Barrier);
        Tls = new(installation, fixture.State, fixture.First.Time);
        AcceptedEnrollmentIssuer issuer = fixture.State.Current!.OnboardingContracts!.Issuers!.Values.Single();
        X509Certificate2 root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(issuer.CertificateDer));
        Tls.Install(installation.Current!.RuntimeGeneration, certificate, root, [],
            leaf => EnrollmentLiveTrust.Validate(fixture.State.Current!, leaf, fixture.First.Time.Now), issuer.Epoch);
        Limits = new(fixture.First.Time);
        sender = new(View, Readiness, new UnavailableSenderConsensus(), fixture.First.Time);
        Runtime = new(services, configured, new(Recovery, Readiness), View, Readiness, sender,
            new(fixture.First.Time), fixture.First.Time, trust, NullLogger<ClusterPeerRuntime>.Instance, Handle);
        inbox = new(Path.Combine(local.StateDirectory, "routing-inbox"), 1000, fixture.First.Time.Now);
        typeof(ClusterPeerRuntime).GetField("inbox", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Runtime, inbox);
    }

    public void SetLeader(EnrollmentRoutingNode leader, RevisionImage image, long term)
    {
        if (Native.Store.Term != term)
        {
            Native.Store.UpdateTermAsync(term, true, CancellationToken.None).GetAwaiter().GetResult();
        }

        Native.Remote = leader.Options.NodeId != Options.NodeId;
        Native.LeaderId = ClusterMemberId.FromEndPoint(new UriEndPoint(new Uri(leader.Options.PublicUri)));
        CancellationToken leadership = CancellationToken.None;
        if (Native.Remote)
        {
            leadership = new CancellationToken(true);
        }

        View.Publish(new(image.ClusterId, image.Generation, Options.NodeId, term, 17, !Native.Remote, image.Index,
            image.Operations.Nodes.Values.Where(node => node.Voting && !node.Removed).Select(node => node.NodeId).ToHashSet(),
            leadership, CancellationToken.None));
        Readiness.ConfirmRecovery(Readiness.RecoveryVersion, View.Accepted, View.Consensus!);
    }

    public void Dispose()
    {
        Runtime.Dispose();
        inbox.Dispose();
        client.Dispose();
        sender.Dispose();
        services.Dispose();
        nativeState.Dispose();
        Tls.Certificate?.Dispose();
        if (ownsLease)
        {
            Lease.Dispose();
        }
    }

    private class Clients(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            if (name != PeerClientHandler.CLIENT_NAME)
            {
                throw new InvalidOperationException("TestUnexpectedClient");
            }

            return client;
        }
    }

    private class Consensus(EnrollmentRoutingFixture fixture, Guid node) : IConsensusWriter
    {
        public async ValueTask<long> ReplicateAsync(ReadOnlyMemory<byte> bytes, int commandId, CancellationToken token)
        {
            long index = fixture.State.Current!.Index + 1;
            RevisionApplyEngine apply = new(fixture.State, new(), fixture.Projection);
            await apply.ApplyAsync(RevisionCodec.Decode(bytes.Span), index, fixture.Term, node, token);
            fixture.Refresh();
            return index;
        }

        public ValueTask<bool> ResignAsync(CancellationToken token)
        {
            throw new NotSupportedException();
        }
    }
}
