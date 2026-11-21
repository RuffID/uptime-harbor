using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Tests.Infrastructure.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster.Renewal;

public class VoterCredentialCryptoScenario : IDisposable
{
    public EnrollmentCertificateFixture Crypto { get; } = new();
    public ECDsa NewKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public X509Certificate2 Previous { get; }
    public X509Certificate2 Next { get; }
    public DateTimeOffset Now { get; }
    public byte[] Csr { get; }
    public VoterCredentialRenewal Intent { get; }
    public RevisionImage State { get; set; }

    public VoterCredentialCryptoScenario()
    {
        Previous = Crypto.Issue();
        Now = Crypto.Now.AddDays(23);
        Csr = new CertificateRequest("CN=rotation", NewKey, HashAlgorithmName.SHA256).CreateSigningRequest();
        EnrollmentRequest request = Crypto.Request with
        {
            SpkiDigest = EnrollmentCertificates.Hash(NewKey.ExportSubjectPublicKeyInfo()), CsrDigest = EnrollmentCertificates.Hash(Csr)
        };
        EnrollmentIssuance issuance = Crypto.Template with
        {
            IssueId = Guid.NewGuid(), SpkiDigest = request.SpkiDigest, CsrDigest = request.CsrDigest,
            NotBeforeUtc = Now.AddMinutes(-2), NotAfterUtc = Now.AddDays(30)
        };
        Next = EnrollmentCertificates.IssueLeaf(Crypto.Issuer, request, issuance, Csr);
        OperationalCredential old = new(Crypto.Template.IssueId, request.NodeId, EnrollmentCertificates.Hash(Previous.RawData),
            Convert.ToBase64String(Previous.RawData), Crypto.Template.NotAfterUtc, null, null);
        Intent = new(old.Id, old.Fingerprint, Crypto.Request.SpkiDigest, 0, issuance);
        AcceptedEnrollmentIssuer issuer = new(Crypto.Epoch, request.NodeId, request.StorageGeneration, EnrollmentIssuer.PEER_PURPOSE,
            Convert.ToBase64String(Crypto.Issuer.RawData), EnrollmentCertificates.Hash(Crypto.Issuer.RawData),
            EnrollmentCertificates.Hash(Crypto.IssuerKey.ExportSubjectPublicKeyInfo()), Crypto.Now, Crypto.Now.AddYears(3), 1);
        OperationalNode node = new(request.NodeId, request.StorageGeneration, request.PeerUri, "voter", true, true, false, 1);
        State = new(request.ClusterId, request.Generation)
        {
            Index = 1, Term = 1,
            Operations = new() { Nodes = ImmutableDictionary<Guid, OperationalNode>.Empty.Add(node.NodeId, node),
                Credentials = ImmutableDictionary<Guid, OperationalCredential>.Empty.Add(old.Id, old), Revision = 1, MembershipVersion = 7 },
            OnboardingContracts = new() { Issuers = ImmutableDictionary<Guid, AcceptedEnrollmentIssuer>.Empty.Add(issuer.Epoch, issuer) }
        };
        OnboardingSnapshot.Validate(State);
    }

    public RevisionCommand Command(string action)
    {
        OperationalCredential? bound = null;
        string? proof = null;
        if (action == "BindVoterCredential")
        {
            bound = new(Intent.Issuance.IssueId, Intent.Issuance.NodeId, EnrollmentCertificates.Hash(Next.RawData),
                Convert.ToBase64String(Next.RawData), Intent.Issuance.NotAfterUtc, null, null);
        }
        else if (action == "RetireOldCredential")
        {
            proof = new string('E', 64);
        }

        return new()
        {
            Schema = 3, ClusterId = State.ClusterId, ClusterGeneration = State.Generation, OperationId = Guid.NewGuid(),
            OriginNodeId = Intent.Issuance.NodeId, ActorUserId = Guid.Empty, ActorRevision = 0,
            EntityId = Intent.Issuance.NodeId, EntityKind = DeletedEntityKind.Node, BaseRevision = 0, Action = action,
            CreatedAtUtc = Now, SystemSource = "VoterCredentialLifecycle",
            EnrollmentTrust = new(1, Intent.Issuance.StorageGeneration, Crypto.Epoch, null, null, null, null, 0, null)
            {
                Rotation = new(Intent, bound, proof), CoordinatorAtUtc = Now, CoordinatorTerm = 1
            }
        };
    }

    public CommandResult Apply(RevisionCommand command)
    {
        State = new RevisionReducer().Apply(State, command, State.Index + 1, State.Term);
        return State.Results[command.OperationId];
    }

    public RevisionCommand Commit(string action)
    {
        RevisionCommand command = Command(action);
        Assert.Equal(OperationOutcome.Committed, Apply(command).Outcome);
        return command;
    }

    public RevisionImage Archived()
    {
        RecoveryBaseline baseline = new(Guid.NewGuid(), State.ClusterId, State.Generation, State.Index, State.Term,
            new string('A', 64), State.Index, Guid.NewGuid(), Guid.NewGuid(), new string('B', 64), Now, State.Operations)
        {
            PreviousOnboardingContracts = State.OnboardingContracts
        };
        return new(Guid.NewGuid(), 2)
        {
            Index = State.Index, Term = State.Term, Results = State.Results, RecoveryBaseline = baseline
        };
    }

    public void Dispose()
    {
        Next.Dispose();
        Previous.Dispose();
        NewKey.Dispose();
        Crypto.Dispose();
    }
}
