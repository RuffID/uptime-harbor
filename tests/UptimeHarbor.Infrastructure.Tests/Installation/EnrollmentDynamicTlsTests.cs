using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class EnrollmentDynamicTlsTests
{
    [Fact]
    public void CurrentAcceptedCredentialAndIssuerRevocationAreCheckedAfterTlsInstallation()
    {
        // Arrange
        using EnrollmentTrustFixture fixture = new();
        RevisionImage image = fixture.Accepted();
        RevisionStateStore accepted = new();
        accepted.Publish(image);
        var credential = Assert.Single(image.Operations.Credentials).Value;
        var node = Assert.Single(image.Operations.Nodes).Value;
        var issuer = Assert.Single(image.OnboardingContracts!.Issuers!).Value;
        using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(credential.PublicCertificate));
        using X509Certificate2 localCertificate = leaf.CopyWithPrivateKey(fixture.Crypto.PeerKey);
        InstallationState installation = new();
        installation.Publish(new(Guid.NewGuid(), "Bootstrap", InstallationPhase.ClusterQuarantine,
            1, 1, 1, Guid.NewGuid(), fixture.Crypto.Now.AddMinutes(2), TrustAnchorFingerprint: issuer.CertificateDigest));
        DynamicPeerTls tls = new(installation, accepted, new FixedTime(fixture.Crypto.Now));
        tls.Install(1, localCertificate, fixture.Crypto.Issuer, [credential.Fingerprint], _ => true, issuer.Epoch);

        // Act
        bool valid = tls.Validate(leaf);
        bool nameMismatch = EnrollmentPeerProbeTransport.ValidateServer(leaf, SslPolicyErrors.RemoteCertificateNameMismatch,
            image, node.NodeId, fixture.Crypto.Now);
        bool customChain = EnrollmentPeerProbeTransport.ValidateServer(leaf, SslPolicyErrors.RemoteCertificateChainErrors,
            image, node.NodeId, fixture.Crypto.Now);
        bool foreignNode = EnrollmentPeerProbeTransport.ValidateServer(leaf, SslPolicyErrors.None,
            image, Guid.NewGuid(), fixture.Crypto.Now);
        accepted.Publish(image with { Operations = image.Operations with
        {
            Credentials = image.Operations.Credentials.SetItem(credential.Id, credential with { RevokedAtUtc = fixture.Crypto.Now })
        } });
        bool revokedCredential = tls.Validate(leaf);
        accepted.Publish(image with { OnboardingContracts = image.OnboardingContracts with
        {
            Issuers = image.OnboardingContracts.Issuers!.SetItem(issuer.Epoch, issuer with { RevokedAtUtc = fixture.Crypto.Now })
        } });
        bool revokedIssuer = tls.Validate(leaf);
        accepted.Publish(image with { Operations = image.Operations with
        {
            Nodes = image.Operations.Nodes.Remove(node.NodeId)
        } });
        bool orphan = tls.Validate(leaf);

        // Assert
        Assert.True(valid);
        Assert.True(customChain);
        Assert.False(nameMismatch);
        Assert.False(foreignNode);
        Assert.False(revokedCredential);
        Assert.False(revokedIssuer);
        Assert.False(orphan);
        Assert.False(EnrollmentPeerProbeTransport.ValidateServer(leaf, SslPolicyErrors.None, image, node.NodeId,
            fixture.Crypto.Template.NotAfterUtc));
    }

    private class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }
}
