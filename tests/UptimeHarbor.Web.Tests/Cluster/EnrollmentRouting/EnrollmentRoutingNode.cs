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
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
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
    public VoterCredentialPeerService Renewal { get; }
    public VoterCredentialAdmission RotationAdmission { get; }
    public VoterCredentialAdmissionClient RotationClient { get; }
    public VoterCredentialIssuanceRouter VoterRouter { get; }
    public EnrollmentGateway Gateway { get; }
    public EnrollmentDescriptors Descriptors { get; }
    public EnrollmentNonces Nonces { get; }
    public EnrollmentCommandReceiver Receiver { get; }
    public EnrollmentIssuanceRouter Router { get; }
    public ManagedAdmissionState Admission { get; }
    public OnlineGrantRequests GrantRequests { get; }
    public RevisionDispatcher Dispatcher { get; }
    public ClusterPeerRuntime Runtime { get; }
    public DynamicPeerTls Tls { get; }
    public EnrollmentRateLimiter Limits { get; }
    internal IHttpClientFactory PeerClients { get; }
    internal IConsensusWriter Writer { get; }
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
        Writer = new Consensus(fixture, options.NodeId);
        Dispatcher = new(Writer, fixture.State, View, time: fixture.First.Time, onboarding: acceptance);
        Receiver = new(authority, Dispatcher, fixture.State, View, Barrier, configured, configuredLocal, fixture.First.Time);
        EnrollmentIssuerStorage storage = new(configuredLocal, configured, lease, secrets);
        EnrollmentSigningJournal journal = new(store, new());
        EnrollmentIssuerSigningLock signingLock = new();
        EnrollmentSigner signer = new(store, journal, signingLock, storage, fixture.First.Time,
            NullLogger<EnrollmentSigner>.Instance, new EnrollmentSigningAuthorization(Handle, View, fixture.State, Readiness, configured, fixture.First.Time));
        Admission = new(installation, configuredLocal, Microsoft.Extensions.Options.Options.Create(new ClusterControlOptions()), lease);
        GrantRequests = new(store, installation, lease, secrets, Admission, Readiness, fixture.State, configured, fixture.First.Time);
        OnlineGrantSigningAuthorization grantAuthority = new(Handle, View, fixture.State, Readiness, configured, fixture.First.Time, Admission, lease);
        OnlineGrantSigner grants = new(store, grantAuthority, new(), storage, fixture.First.Time);
        Service = new(Receiver, signer, grants, fixture.State, Barrier, configured, fixture.First.Time);
        AcceptedPeerTrustSource trust = new(fixture.State, fixture.First.Time);
        Tls = new(installation, fixture.State, fixture.First.Time);
        AcceptedEnrollmentIssuer issuer = fixture.State.Current!.OnboardingContracts!.Issuers!.Values.Single();
        X509Certificate2 root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(issuer.CertificateDer));
        Tls.Install(installation.Current!.RuntimeGeneration, certificate, root, [],
            leaf => EnrollmentLiveTrust.Validate(fixture.State.Current!, leaf, fixture.First.Time.Now), issuer.Epoch);
        DynamicPeerTls? outboundTls = null;
        HttpMessageHandler transport;
        if (fixture.UseManagedPools)
        {
            outboundTls = Tls;
            transport = new ManagedPeerTransport(Tls, trust, fixture.First.Time,
                (_, pooled, confirm) => new EnrollmentRoutingHandler(fixture, this, pooled, confirm));
        }
        else
        {
            transport = new EnrollmentRoutingHandler(fixture, this);
        }

        PeerClientHandler authenticated = new(configured, trust, fixture.First.Time, View, configuredLocal, outboundTls)
        {
            InnerHandler = transport
        };
        client = new(authenticated);
        PeerClients = new Clients(client);
        Client = new(Handle, View, fixture.State, Barrier, Receiver, trust, PeerClients, configured, fixture.First.Time, outboundTls);
        Router = new(signer, grants, fixture.State, Client, configured, fixture.First.Time, GrantRequests);
        Descriptors = new(fixture.State, configuredLocal, configured, fixture.First.Time);
        Nonces = new(fixture.First.Time);
        VoterCredentialRenewalAuthority renewal = new(fixture.State, Barrier, Nonces, configured, fixture.First.Time);
        VoterCredentialSigner voterSigner = new(store, journal, signingLock, storage, renewal, configured, fixture.First.Time,
            NullLogger<VoterCredentialSigner>.Instance);
        Renewal = new(voterSigner, renewal, View, configured, fixture.First.Time, Tls);
        RotationAdmission = new(renewal, Dispatcher, fixture.State, View, Nonces, installation, Tls, configured, fixture.First.Time);
        RotationClient = new(Client, RotationAdmission, fixture.State, View, Barrier, installation, Tls, configured, fixture.First.Time);
        VoterRouter = new(voterSigner, Client, renewal, configured, fixture.First.Time);
        Gateway = new(Descriptors, Nonces, new(), new(store, installation, fixture.First.Time), Client, Router,
            configured, configuredLocal, fixture.First.Time, Barrier);
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
