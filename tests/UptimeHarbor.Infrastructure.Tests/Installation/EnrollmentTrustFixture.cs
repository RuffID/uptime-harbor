using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class EnrollmentTrustFixture : IDisposable
{
    public EnrollmentCertificateFixture Crypto { get; } = new();
    public RevisionCommand Account { get; }
    public GenesisAuthorization Genesis { get; }
    public RevisionCommand Initialize { get; }
    public RevisionImage Empty { get; }

    public EnrollmentTrustFixture()
    {
        EnrollmentRequest request = Crypto.Request;
        Guid accountId = Guid.NewGuid();
        Account = new()
        {
            ClusterId = request.ClusterId, ClusterGeneration = request.Generation, OperationId = Guid.NewGuid(),
            OriginNodeId = request.NodeId, ActorUserId = accountId, ActorRevision = 1, EntityId = accountId,
            EntityKind = DeletedEntityKind.Account, Action = "BootstrapAccount", BaseRevision = 0, CreatedAtUtc = Crypto.Now,
            Value = new() { Name = "admin", RoleId = Guid.NewGuid(), PasswordHash = "hash", SecurityStamp = "stamp", ConcurrencyStamp = "concurrency" }
        };
        Genesis = new(1, Guid.NewGuid(), Guid.NewGuid(), request.ClusterId, request.Generation, request.NodeId,
            request.StorageGeneration, request.PeerUri, EnrollmentCertificates.Hash(Crypto.IssuerKey.ExportSubjectPublicKeyInfo()),
            EnrollmentCertificates.Hash(Crypto.Issuer.RawData), RevisionCodec.Digest(Account), null);
        AcceptedEnrollmentIssuer issuer = new(Crypto.Epoch, request.NodeId, request.StorageGeneration,
            EnrollmentIssuer.PEER_PURPOSE, Convert.ToBase64String(Crypto.Issuer.RawData), Genesis.IssuerCertificateDigest,
            Genesis.IssuerSpkiDigest, Crypto.Now, Crypto.Now.AddYears(3), 0);
        using X509Certificate2 leaf = Crypto.Issue();
        OperationalNode node = new(request.NodeId, request.StorageGeneration, request.PeerUri, "native-member", true, false, false, 0);
        OperationalCredential credential = new(Crypto.Template.IssueId, request.NodeId, EnrollmentCertificates.Hash(leaf.RawData),
            Convert.ToBase64String(leaf.RawData), Crypto.Template.NotAfterUtc, null, null);
        Initialize = new()
        {
            Schema = 3, ClusterId = request.ClusterId, ClusterGeneration = request.Generation,
            OperationId = Genesis.GenesisId, OriginNodeId = request.NodeId, ActorUserId = Guid.Empty, ActorRevision = 0,
            EntityId = issuer.Epoch, EntityKind = DeletedEntityKind.Node, Action = "InitializeCluster", BaseRevision = 0,
            SystemSource = "GenesisController", CreatedAtUtc = Crypto.Now,
            EnrollmentTrust = new(1, request.StorageGeneration, issuer.Epoch, issuer, Genesis, node, credential, 0, null)
            {
                CoordinatorAtUtc = Crypto.Now, CoordinatorTerm = 1
            }
        };
        Empty = new(request.ClusterId, request.Generation)
        {
            BootstrapAccountDigest = Genesis.AccountOrImportDigest,
            OnboardingContracts = new() { Genesis = Genesis }
        };
    }

    public RevisionImage Accepted()
    {
        RevisionReducer reducer = new();
        RevisionImage initialized = reducer.Apply(Empty, Initialize, 1, 1);
        return reducer.Apply(initialized, Account, 2, 1);
    }

    public RevisionCommand Revoke(RevisionImage image)
    {
        return Initialize with
        {
            OperationId = Guid.NewGuid(), Action = "RevokeEnrollmentIssuer", SystemSource = null,
            ActorUserId = Account.ActorUserId, ActorRevision = image.Values[Account.EntityId].Revision,
            BaseRevision = image.OnboardingContracts!.Issuers![Crypto.Epoch].Revision,
            EnrollmentTrust = Initialize.EnrollmentTrust! with
            {
                Issuer = null, Genesis = null, Node = null, Credential = null, ReauthenticatedAtUtc = Crypto.Now
            }
        };
    }

    public void Dispose()
    {
        Crypto.Dispose();
    }
}
