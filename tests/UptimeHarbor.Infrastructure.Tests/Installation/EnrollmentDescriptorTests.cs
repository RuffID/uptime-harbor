using System.Collections.Immutable;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class EnrollmentDescriptorTests
{
    [Theory]
    [InlineData("issue")]
    [InlineData("serial")]
    [InlineData("der")]
    public void PreviouslyReceivedIssuanceAndDerCannotBeReplacedEvenByValidIssuer(string mutation)
    {
        // Arrange
        using Fixture fixture = new();
        EnrollmentIssuance template = fixture.Crypto.Template;
        if (mutation == "issue")
        {
            template = template with { IssueId = Guid.NewGuid() };
        }
        else if (mutation == "serial")
        {
            template = template with { Serial = "1123456789ABCDEF0123456789ABCDEF" };
        }

        using X509Certificate2 replacement = EnrollmentCertificates.IssueLeaf(fixture.Crypto.Issuer,
            fixture.Crypto.Request, template, fixture.Crypto.Csr);
        EnrollmentDescriptor changed = fixture.Descriptor with
        {
            Issuance = template, CertificateDer = Convert.ToBase64String(replacement.RawData),
            Credentials = [fixture.Descriptor.Credentials[0] with
            {
                Id = template.IssueId, Fingerprint = EnrollmentCertificates.Hash(replacement.RawData),
                PublicCertificate = Convert.ToBase64String(replacement.RawData)
            }]
        };

        // Act
        Exception? error = Record.Exception(() => fixture.Validate(changed, fixture.Local with { Descriptor = fixture.Descriptor }));

        // Assert
        Assert.IsType<InvalidDataException>(error);
        Assert.NotEqual(fixture.Descriptor.CertificateDer, changed.CertificateDer);
    }

    [Theory]
    [InlineData("origin")]
    [InlineData("capability")]
    [InlineData("cluster")]
    [InlineData("digest")]
    [InlineData("csr")]
    [InlineData("pin")]
    [InlineData("missing-leaf")]
    [InlineData("voter")]
    [InlineData("activated")]
    public void InvalidDescriptorCannotProvideAdmittedTrust(string mutation)
    {
        // Arrange
        using Fixture fixture = new();
        EnrollmentDescriptor changed = fixture.Descriptor;
        switch (mutation)
        {
            case "origin":
                changed = changed with { Discovery = changed.Discovery with { BrowserOrigin = "https://other.example/" } };
                break;
            case "capability":
                changed = changed with { Discovery = changed.Discovery with { Capabilities = [] } };
                break;
            case "cluster":
                changed = changed with { Discovery = changed.Discovery with { ClusterId = Guid.NewGuid() } };
                break;
            case "digest":
                changed = changed with { RequestDigest = new string('0', 64) };
                break;
            case "csr":
                changed = changed with { Issuance = changed.Issuance! with { CsrDigest = new string('0', 64) } };
                break;
            case "pin":
                changed = changed with { Credentials = [changed.Credentials[0] with { Fingerprint = new string('0', 64) }] };
                break;
            case "missing-leaf":
                changed = changed with { CertificateDer = null };
                break;
            case "voter":
                changed = changed with { Nodes = [changed.Nodes[0] with { Voting = true }] };
                break;
            case "activated":
                changed = changed with { Nodes = [changed.Nodes[0] with { Activated = true }] };
                break;
        }

        // Act
        Exception? error = Record.Exception(() => fixture.Validate(changed, fixture.Local));

        // Assert
        Assert.IsType<InvalidDataException>(error);
    }

    [Fact]
    public void ExactAdmittedDescriptorRemainsValidOnRetry()
    {
        // Arrange
        using Fixture fixture = new();

        // Act
        Exception? error = Record.Exception(() => fixture.Validate(fixture.Descriptor,
            fixture.Local with { Descriptor = fixture.Descriptor }));

        // Assert
        Assert.Null(error);
    }

    [Theory]
    [InlineData("ReadEnrollmentAttempt")]
    [InlineData("ResumeOnboardingAttempt")]
    [InlineData("RenewOnboardingProcess")]
    public void PreBindProcessReplyKeepsPreviouslyReceivedDer(string action)
    {
        // Arrange
        using Fixture fixture = new();
        EnrollmentDescriptor previous = fixture.Descriptor with { Status = "CertificateAuthorized", AdmissionUntilUtc = null };
        EnrollmentProof proof = previous.ResponseProof! with { Action = action };
        EnrollmentDescriptor reply = previous with { CertificateDer = null, ResponseProof = proof };

        // Act
        EnrollmentDescriptor validated = EnrollmentDescriptorValidation.Bound(reply,
            fixture.Local with { Descriptor = previous }, proof, new(new SetupPeerAddressPolicy(new())), fixture.Crypto.Now);

        // Assert
        Assert.Equal(previous.CertificateDer, validated.CertificateDer);
        Assert.Equal(previous.Issuance, validated.Issuance);
        Assert.Equal(proof, validated.ResponseProof);
    }

    private class Fixture : IDisposable
    {
        public EnrollmentCertificateFixture Crypto { get; } = new();
        public EnrollmentDescriptor Descriptor { get; }
        public LocalOnboardingAttempt Local { get; }

        public Fixture()
        {
            EnrollmentRequest request = Crypto.Request;
            EnrollmentProof proof = new(1, Guid.NewGuid(), "AdmitInvitedNode", Guid.NewGuid(),
                Convert.ToBase64String(new byte[32]), 1, Convert.ToBase64String(new byte[32]),
                EnrollmentRequestCodec.Digest(request), "proof");
            EnrollmentDiscovery discovery = new(1, OnboardingProtocol.TRUST_COMPATIBILITY,
                ["csr-p256", "pop-nonce-v1", "accepted-reserve-bind-admit", "process-resume-v1"],
                request.ClusterId, request.Generation, request.InvitationId, "https://first.example/",
                "https://first.example/internal/enrollment/v1/", "https://first-peer.example/internal/raft", 65536, 10);
            AcceptedEnrollmentIssuer issuer = new(Crypto.Epoch, request.NodeId, request.StorageGeneration,
                EnrollmentIssuer.PEER_PURPOSE, Convert.ToBase64String(Crypto.Issuer.RawData),
                EnrollmentCertificates.Hash(Crypto.Issuer.RawData),
                EnrollmentCertificates.Hash(Crypto.IssuerKey.ExportSubjectPublicKeyInfo()), Crypto.Now, Crypto.Now.AddYears(3), 1);
            using X509Certificate2 leaf = Crypto.Issue();

            OperationalCredential credential = new(Crypto.Template.IssueId, request.NodeId,
                EnrollmentCertificates.Hash(leaf.RawData), Convert.ToBase64String(leaf.RawData),
                Crypto.Template.NotAfterUtc, proof.RequestId, Crypto.Now.AddMinutes(10));
            Descriptor = new(1, discovery, request, proof.RequestDigest, proof.BootId, proof.BootChallenge,
                1, proof.Nonce, 1, 4, 123, Crypto.Now.AddMinutes(30), Crypto.Now.AddMinutes(2),
                Crypto.Now.AddMinutes(10), "Admitted", Crypto.Template, credential.PublicCertificate,
                [issuer], [new(request.NodeId, request.StorageGeneration, request.PeerUri, "member", false, false, false, 0)],
                [credential]) { ResponseProof = proof };
            Local = new(request.AttemptId, request, proof.RequestDigest, "key-ref", 1,
                ImmutableDictionary<string, Guid>.Empty, null, 1) { BrowserOrigin = discovery.BrowserOrigin };
        }

        public void Validate(EnrollmentDescriptor descriptor, LocalOnboardingAttempt local)
        {
            EnrollmentDescriptorValidation.Bound(descriptor, local, Descriptor.ResponseProof!,
                new(new SetupPeerAddressPolicy(new())), Crypto.Now);
        }

        public void Dispose()
        {
            Crypto.Dispose();
        }
    }
}
