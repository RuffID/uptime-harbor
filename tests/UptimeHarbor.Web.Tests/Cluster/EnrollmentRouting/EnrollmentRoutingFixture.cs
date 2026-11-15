using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DotNext.Net.Cluster;
using Microsoft.AspNetCore.Connections;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

/// <summary>Три роли над одним синтетическим accepted image, память вместо репликации/SQL.</summary>
public class EnrollmentRoutingFixture : IAsyncDisposable
{
    public ManagedBootstrapFixture First { get; } = new();
    public RevisionStateStore State => First.Accepted;
    public InvitationHttpFixture.Projection Projection { get; } = new();
    public List<EnrollmentRoutingNode> Nodes { get; } = [];
    public EnrollmentRoutingNode Issuer => Nodes[0];
    public EnrollmentRoutingNode Leader { get; private set; } = null!;
    public EnrollmentRoutingNode Ingress => Nodes[2];
    public List<(Guid Source, Guid Target, string Path, byte[] Body)> Sent { get; } = [];
    public bool OfflineSigner { get; set; }
    public string? LoseResponse { get; set; }
    public Action<EnrollmentRoutingNode, string>? BeforeSend { get; set; }
    public Action<EnrollmentRoutingNode, string>? AfterSend { get; set; }
    public Func<string, byte[], byte[]>? EditResponse { get; set; }
    public long Term { get; set; } = 1;
    private readonly List<InstallationLease> preparedLeases = [];

    public async Task InitializeAsync(CancellationToken token)
    {
        await EnrollmentGatewayTests.ReadyAsync(First, token);
        Prepared leader = PrepareVoter("leader");
        Prepared ingress = PrepareVoter("ingress");
        State.Publish(Add(State.Current!, leader, ingress));
        Nodes.Add(new(this, First.Options, First.Local, First.Lease, First.Installation, First.Secrets,
            First.Store, First.Tls.Certificate!, false));
        Nodes.Add(new(this, leader.Options, leader.Local, leader.Lease, leader.Installation, leader.Secrets, leader.Store, leader.Certificate, true));
        Nodes.Add(new(this, ingress.Options, ingress.Local, ingress.Lease, ingress.Installation, ingress.Secrets, ingress.Store, ingress.Certificate, true));
        SetLeader(Nodes[1]);
    }

    public void SetLeader(EnrollmentRoutingNode node)
    {
        Leader = node;
        Refresh();
    }

    public void Refresh()
    {
        foreach (EnrollmentRoutingNode node in Nodes)
        {
            node.SetLeader(Leader, State.Current!, Term);
        }
    }

    public async Task<JoiningKey> InviteAsync(CancellationToken token)
    {
        JoiningKey joining = new(First.Options);
        RevisionImage image = State.Current!;
        RevisionCommand command = new()
        {
            Schema = 3, ClusterId = image.ClusterId, ClusterGeneration = image.Generation, OperationId = Guid.NewGuid(),
            OriginNodeId = Leader.Options.NodeId, ActorUserId = First.Account.ActorUserId,
            ActorRevision = image.Values[First.Account.ActorUserId].Revision, EntityId = joining.Request.InvitationId,
            EntityKind = DeletedEntityKind.Node, Action = "IssueInvitation", BaseRevision = image.Onboarding?.Revision ?? 0,
            CreatedAtUtc = First.Time.Now, Onboarding = new()
            {
                StorageGeneration = Leader.Options.StorageGeneration, InvitationId = joining.Request.InvitationId,
                Verifier = InvitationSecrets.Verifier(image.ClusterId, image.Generation, joining.Request.InvitationId, joining.Code),
                ReauthenticatedAtUtc = First.Time.Now
            }
        };
        CommandResult result = await Leader.Dispatcher.AcceptAsync(command, Leader.Options.NodeId, token);
        if (result.Outcome != OperationOutcome.Committed)
        {
            throw new InvalidOperationException("TestInvitationRejected/" + result.Reason);
        }

        return joining;
    }

    private Prepared PrepareVoter(string name)
    {
        InstallationOptions local = new()
        {
            InstallationIntent = "Setup", StateDirectory = Path.Combine(First.Root, name, "state"),
            SecretDirectory = Path.Combine(First.Root, name, "secret"), KeyDirectory = Path.Combine(First.Root, name, "keys"),
            BrowserOrigin = $"https://{name}-browser.example.test", PeerEndpoint = $"https://{name}.example.test:8443/internal/raft"
        };
        ClusterOptions options = new()
        {
            Mode = "Restart", ClusterId = First.Options.ClusterId, Generation = 1, NodeId = Guid.NewGuid(), StorageGeneration = Guid.NewGuid(),
            CredentialId = Guid.NewGuid(), PublicUri = local.PeerEndpoint, PeerListenPort = 8443,
            ProtocolMajor = 3, CommandSchema = 3, SnapshotFormat = 3, SqlSchema = 3,
            StoragePath = Path.Combine(First.Root, name, "storage"), ExpectedBotId = First.Options.ExpectedBotId
        };
        InstallationLease lease = new(local, options.StoragePath);
        preparedLeases.Add(lease);
        LocalSecretStore secrets = new(Microsoft.Extensions.Options.Options.Create(local), lease);
        Guid attempt = Guid.NewGuid();
        using ECDsa peerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] csr = new CertificateRequest("CN=synthetic voter", peerKey, HashAlgorithmName.SHA256).CreateSigningRequest();
        byte[] pem = Encoding.UTF8.GetBytes(peerKey.ExportPkcs8PrivateKeyPem());
        string keyReference = secrets.Write(attempt, "peer", pem);
        CryptographicOperations.ZeroMemory(pem);
        EnrollmentIssuerReference issuer = First.Store.Issuers.Values.Single();
        byte[] issuerPem = First.Secrets.Read(issuer.Epoch, "issuer", issuer.KeyReference);
        X509Certificate2 certificate;
        try
        {
            using ECDsa issuerKey = ECDsa.Create();
            issuerKey.ImportFromPem(Encoding.UTF8.GetString(issuerPem));
            using X509Certificate2 root = X509CertificateLoader.LoadCertificate(issuer.CertificateDer);
            using X509Certificate2 signing = root.CopyWithPrivateKey(issuerKey);
            EnrollmentRequest request = new(1, options.ClusterId, 1, attempt, attempt, options.NodeId, options.StorageGeneration,
                options.PublicUri, EnrollmentCertificates.Hash(peerKey.ExportSubjectPublicKeyInfo()), EnrollmentCertificates.Hash(csr));
            EnrollmentIssuance template = new(1, options.CredentialId, issuer.Epoch, new string('1', 32), options.NodeId,
                options.StorageGeneration, options.PublicUri, request.SpkiDigest, request.CsrDigest,
                First.Time.Now, First.Time.Now.AddDays(30));
            using X509Certificate2 leaf = EnrollmentCertificates.IssueLeaf(signing, request, template, csr);
            certificate = leaf.CopyWithPrivateKey(peerKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(issuerPem);
        }

        ManagedBootstrapStore store = new()
        {
            Profile = new(lease.InstallIdentity, "Join", InstallationPhase.ClusterReady, 1, 1, 1,
                lease.BootId, First.Time.Now.AddMinutes(2))
            {
                AttemptId = attempt, NodeId = options.NodeId, StorageGeneration = options.StorageGeneration, PeerAddress = options.PublicUri,
                ClusterId = options.ClusterId, ClusterGeneration = 1, KeyReference = keyReference, CredentialId = options.CredentialId,
                TrustAnchorFingerprint = issuer.CertificateDigest, RequestDigest = new string('A', 64), OperationIds = ImmutableDictionary<string, Guid>.Empty
            }
        };
        InstallationState installation = new();
        installation.Publish(store.Profile);
        installation.ConfirmCommonOpen(store.Profile);
        return new(options, local, lease, installation, secrets, store, certificate);
    }

    private static RevisionImage Add(RevisionImage image, params Prepared[] voters)
    {
        foreach (Prepared voter in voters)
        {
            ClusterOptions options = voter.Options;
            OperationalNode node = new(options.NodeId, options.StorageGeneration, options.PublicUri,
                ClusterMemberId.FromEndPoint(new UriEndPoint(new Uri(options.PublicUri))).ToString(), true, true, false, image.Index);
            OperationalCredential credential = new(options.CredentialId, node.NodeId, EnrollmentCertificates.Hash(voter.Certificate.RawData),
                Convert.ToBase64String(voter.Certificate.RawData), new DateTimeOffset(voter.Certificate.NotAfter.ToUniversalTime()), null, null);
            image = image with
            {
                Operations = image.Operations with
                {
                    Nodes = image.Operations.Nodes.Add(node.NodeId, node), Credentials = image.Operations.Credentials.Add(credential.Id, credential)
                },
                Monitoring = image.Monitoring with { Nodes = image.Monitoring.Nodes.Add(node.NodeId, node.StorageGeneration) }
            };
        }

        return image;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (EnrollmentRoutingNode node in Nodes.AsEnumerable().Reverse())
        {
            node.Dispose();
        }

        foreach (InstallationLease lease in preparedLeases)
        {
            lease.Dispose();
        }

        await First.DisposeAsync();
    }

    private record Prepared(ClusterOptions Options, InstallationOptions Local, InstallationLease Lease,
        InstallationState Installation, LocalSecretStore Secrets, ManagedBootstrapStore Store, X509Certificate2 Certificate);

    public class JoiningKey : IDisposable
    {
        public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public Guid Code { get; } = InvitationSecrets.CreateCode();
        public EnrollmentRequest Request { get; }
        public byte[] Csr { get; }
        public Guid Boot { get; } = Guid.NewGuid();
        public string Challenge { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        public JoiningKey(ClusterOptions cluster)
        {
            Csr = new CertificateRequest("CN=joining", Key, HashAlgorithmName.SHA256).CreateSigningRequest();
            Request = new(1, cluster.ClusterId, 1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                "https://joining.example.test:8443/internal/raft", EnrollmentCertificates.Hash(Key.ExportSubjectPublicKeyInfo()), EnrollmentCertificates.Hash(Csr));
        }

        public EnrollmentExchange Exchange(EnrollmentRoutingNode node, string action, Guid? operation = null)
        {
            string digest = EnrollmentRequestCodec.Digest(Request);
            string nonce = node.Nonces.Issue(Request.AttemptId, Boot, Challenge, 1, action, digest);
            EnrollmentProof proof = EnrollmentProofs.Sign(Request, new(1, operation ?? Guid.NewGuid(), action, Boot, Challenge, 1, nonce, digest, string.Empty), Key);
            string? invitation = null;
            if (action == "ReserveInvitation")
            {
                invitation = Code.ToString("D");
            }

            return new(1, Request, proof, Convert.ToBase64String(Csr), invitation);
        }

        public void Dispose()
        {
            Key.Dispose();
        }
    }
}
