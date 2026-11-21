using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

public class VoterCredentialPeerFixture : IAsyncDisposable
{
    public EnrollmentRoutingFixture Cluster { get; } = new();
    public RevisionImage Authorized { get; private set; } = null!;
    public RevisionImage BeforeAuthorization { get; private set; } = null!;
    public VoterCredentialSigningRequest Call { get; private set; } = null!;
    public int InitialIssueCount { get; private set; }
    public EnrollmentRoutingNode Subject => Cluster.Ingress;
    public EnrollmentRoutingNode Issuer => Cluster.Issuer;
    public Guid IssueId => Call.ProofRequest.Request.Renewal.Issuance.IssueId;

    public async Task InitializeAsync(CancellationToken token)
    {
        Cluster.RenewalEnabled = true;
        await Cluster.InitializeAsync(token);
        InitialIssueCount = Issuer.Store.Issues.Count;
        Cluster.First.Time.Now = Cluster.First.Time.Now.AddDays(25);
        DateTimeOffset now = Cluster.First.Time.Now;
        foreach (EnrollmentRoutingNode node in Cluster.Nodes)
        {
            foreach (string component in new[] { "Database", "Projection", "Storage", "Clock" })
            {
                node.Readiness.Report(component, true, "Ready", TimeSpan.FromDays(1));
            }
        }

        RevisionImage image = Cluster.State.Current!;
        OperationalCredential previous = image.Operations.Credentials[Subject.Options.CredentialId];
        AcceptedEnrollmentIssuer issuer = image.OnboardingContracts!.Issuers!.Values.Single();
        using ECDsa next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa old = Subject.Certificate.GetECDsaPrivateKey()!;
        byte[] csr = new CertificateRequest("CN=renewal voter", next, HashAlgorithmName.SHA256).CreateSigningRequest();
        EnrollmentIssuance template = new(1, Guid.NewGuid(), issuer.Epoch, new string('4', 32),
            Subject.Options.NodeId, Subject.Options.StorageGeneration, Subject.Options.PublicUri,
            EnrollmentCertificates.Hash(next.ExportSubjectPublicKeyInfo()), EnrollmentCertificates.Hash(csr), now, now.AddDays(30));
        VoterCredentialRequest request = new(1, image.ClusterId, image.Generation, Guid.NewGuid(),
            new(previous.Id, previous.Fingerprint, EnrollmentCertificates.Hash(old.ExportSubjectPublicKeyInfo()), 0, template));
        OnlineProcessLease process = new(Subject.Options.NodeId, Guid.NewGuid(), Subject.Lease.BootId,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), 1, 0, now.AddMinutes(2), image.Index);
        image = image with { OnboardingContracts = image.OnboardingContracts with
        {
            OnlineProcesses = image.OnboardingContracts.OnlineProcesses!.SetItem(process.NodeId, process)
        } };

        VoterCredentialProof proof = new(1, VoterCredentialProofs.SCOPE, process.NodeId, Subject.Options.StorageGeneration,
            process.BootId, process.BootChallenge, process.ProcessEpoch, process.RecoveryVersion, image.Operations.MembershipVersion,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), now, string.Empty);
        VoterCredentialProofRequest proofRequest = new(request, VoterCredentialProofs.Sign(request, proof, old), Convert.ToBase64String(csr));
        RevisionCommand command = VoterCredentialAuthorizationCommand.Create(request);
        BeforeAuthorization = image;
        // Синтетический accepted срез через production reducer. Rotation dispatcher в production закрыт.
        Authorized = VoterCredentialReducer.Apply(image with { Index = image.Index + 1 }, command, now);
        Assert.Equal(OperationOutcome.Committed, Authorized.Results[request.OperationId].Outcome);
        Call = new(1, proofRequest, Authorized.Index);
        Publish(Authorized);
    }

    public void Publish(RevisionImage image)
    {
        Cluster.State.Publish(image);
        Cluster.Refresh();
    }

    public X509Certificate2 BindIssuerReplacement(DateTimeOffset? overlapEnd = null)
    {
        DateTimeOffset now = Cluster.First.Time.Now;
        DateTimeOffset bound = overlapEnd?.AddDays(-1) ?? now;
        AcceptedEnrollmentIssuer issuer = Authorized.OnboardingContracts!.Issuers!.Values.Single();
        OperationalCredential previous = Authorized.Operations.Credentials[Issuer.Options.CredentialId];
        EnrollmentIssuerReference issuerKey = Issuer.Store.Issuers[issuer.Epoch];
        using ECDsa replacement = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] csr = new CertificateRequest("CN=issuer replacement", replacement, HashAlgorithmName.SHA256).CreateSigningRequest();
        Guid id = Guid.NewGuid();
        EnrollmentRequest request = new(1, Authorized.ClusterId, Authorized.Generation, id, id,
            Issuer.Options.NodeId, Issuer.Options.StorageGeneration, Issuer.Options.PublicUri,
            EnrollmentCertificates.Hash(replacement.ExportSubjectPublicKeyInfo()), EnrollmentCertificates.Hash(csr));
        EnrollmentIssuance template = new(1, id, issuer.Epoch, new string('7', 32), request.NodeId, request.StorageGeneration,
            request.PeerUri, request.SpkiDigest, request.CsrDigest, bound, bound.AddDays(30));
        byte[] pem = Issuer.Secrets.Read(issuer.Epoch, "issuer", issuerKey.KeyReference);
        try
        {
            using ECDsa signingKey = ECDsa.Create();
            signingKey.ImportFromPem(System.Text.Encoding.UTF8.GetString(pem));
            using X509Certificate2 root = X509CertificateLoader.LoadCertificate(issuerKey.CertificateDer);
            using X509Certificate2 signing = root.CopyWithPrivateKey(signingKey);
            using X509Certificate2 leaf = EnrollmentCertificates.IssueLeaf(signing, request, template, csr);
            OperationalCredential credential = new(id, request.NodeId, EnrollmentCertificates.Hash(leaf.RawData),
                Convert.ToBase64String(leaf.RawData), template.NotAfterUtc, null, null);
            using X509Certificate2 prior = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(previous.PublicCertificate));
            VoterCredentialRenewal renewal = new(previous.Id, previous.Fingerprint,
                EnrollmentCertificates.Hash(prior.PublicKey.ExportSubjectPublicKeyInfo()), 0, template);
            VoterCredentialRequest intent = new(1, Authorized.ClusterId, Authorized.Generation, Guid.NewGuid(), renewal);
            RevisionCommand authorize = VoterCredentialAuthorizationCommand.Create(intent);
            RevisionImage authorized = VoterCredentialReducer.Apply(Authorized with { Index = Authorized.Index + 1 }, authorize, bound);
            CommandResult authorization = authorized.Results[authorize.OperationId];
            Assert.True(authorization.Outcome == OperationOutcome.Committed, authorization.Reason);

            RevisionCommand bind = authorize with
            {
                OperationId = Guid.NewGuid(), Action = "BindVoterCredential",
                EnrollmentTrust = authorize.EnrollmentTrust! with { Rotation = new(renewal, credential, null) }
            };
            RevisionImage accepted = VoterCredentialReducer.Apply(authorized with { Index = authorized.Index + 1 }, bind, bound);
            CommandResult binding = accepted.Results[bind.OperationId];
            Assert.True(binding.Outcome == OperationOutcome.Committed, binding.Reason);
            Publish(accepted);

            return leaf.CopyWithPrivateKey(replacement);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pem);
        }
    }

    public Task<EnrollmentIssuedArtifact> IssueAsync(CancellationToken token)
    {
        return Subject.Client.IssueAsync(Issuer.Options.NodeId, Call, token);
    }

    public DynamicPeerTls.Snapshot PublishIssuerReplacement(X509Certificate2 replacement)
    {
        DynamicPeerTls.Snapshot old = Assert.IsType<DynamicPeerTls.Snapshot>(Issuer.Tls.Capture());
        RevisionImage image = Cluster.State.Current!;
        AcceptedEnrollmentIssuer issuer = image.OnboardingContracts!.Issuers!.Values.Single();
        string fingerprint = EnrollmentCertificates.Hash(replacement.RawData);
        OperationalCredential next = image.Operations.Credentials.Values.Single(item => item.Fingerprint == fingerprint);
        return Issuer.Tls.PublishConfirmed(Cluster.State, image, Cluster.State.RecoveryVersion,
            Issuer.Installation.Current!, next.Id, image.Index, replacement, old.Root, issuer.Epoch);
    }

    public ValueTask DisposeAsync()
    {
        return Cluster.DisposeAsync();
    }
}
