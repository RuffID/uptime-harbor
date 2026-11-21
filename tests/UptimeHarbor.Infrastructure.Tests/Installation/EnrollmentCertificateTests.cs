using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class EnrollmentCertificateTests
{
    [Fact]
    public void DedicatedIssuerAndLeafHaveExactIdentityLifetimeAndBothTlsPurposes()
    {
        // Arrange
        using EnrollmentCertificateFixture fixture = new();

        // Act
        using X509Certificate2 leaf = fixture.Issue();
        bool client = EnrollmentCertificates.ValidateLeaf(leaf, fixture.Issuer, fixture.Request,
            fixture.Template, EnrollmentCertificates.Hash(leaf.RawData), fixture.Now, false);
        bool server = EnrollmentCertificates.ValidateLeaf(leaf, fixture.Issuer, fixture.Request,
            fixture.Template, EnrollmentCertificates.Hash(leaf.RawData), fixture.Now, true);

        // Assert
        Assert.True(client);
        Assert.True(server);
        Assert.False(leaf.HasPrivateKey);
        X509BasicConstraintsExtension ca = Assert.Single(fixture.Issuer.Extensions.OfType<X509BasicConstraintsExtension>());
        Assert.True(ca.CertificateAuthority);
        Assert.True(ca.HasPathLengthConstraint);
        Assert.Equal(0, ca.PathLengthConstraint);
        Assert.Equal(fixture.Now.AddYears(3).UtcDateTime, fixture.Issuer.NotAfter.ToUniversalTime());
    }

    [Fact]
    public void PinExpiryHostAndIssuerSubstitutionAreRejected()
    {
        // Arrange
        using EnrollmentCertificateFixture fixture = new();
        using EnrollmentCertificateFixture other = new();
        using X509Certificate2 leaf = fixture.Issue();
        string pin = EnrollmentCertificates.Hash(leaf.RawData);
        EnrollmentRequest moved = fixture.Request with { PeerUri = "https://other.example.test/internal/raft" };
        EnrollmentIssuance movedTemplate = fixture.Template with { PeerUri = moved.PeerUri };

        // Act
        bool wrongPin = EnrollmentCertificates.ValidateLeaf(leaf, fixture.Issuer, fixture.Request, fixture.Template, new string('F', 64), fixture.Now, true);
        bool expired = EnrollmentCertificates.ValidateLeaf(leaf, fixture.Issuer, fixture.Request, fixture.Template, pin, fixture.Now.AddDays(30), false);
        bool early = EnrollmentCertificates.ValidateLeaf(leaf, fixture.Issuer, fixture.Request, fixture.Template, pin, fixture.Now.AddTicks(-1), false);
        bool host = EnrollmentCertificates.ValidateLeaf(leaf, fixture.Issuer, moved, movedTemplate, pin, fixture.Now, true);
        bool issuer = EnrollmentCertificates.ValidateLeaf(leaf, other.Issuer, fixture.Request, fixture.Template, pin, fixture.Now, false);

        // Assert
        Assert.False(wrongPin);
        Assert.False(expired);
        Assert.False(early);
        Assert.False(host);
        Assert.False(issuer);
    }

    [Fact]
    public void IssuerWithDamagedSelfSignatureIsNotAcceptedAsCustomTrustRoot()
    {
        // Arrange
        using EnrollmentCertificateFixture fixture = new();
        byte[] der = fixture.Issuer.RawData;
        der[^1] ^= 1;
        using X509Certificate2 damaged = X509CertificateLoader.LoadCertificate(der);

        // Act
        bool accepted = EnrollmentCertificates.ValidateIssuer(damaged, fixture.Request.ClusterId, 1, fixture.Epoch, fixture.Now);

        // Assert
        Assert.False(accepted);
    }
}
