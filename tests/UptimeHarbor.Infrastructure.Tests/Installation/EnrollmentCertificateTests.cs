using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class EnrollmentCertificateTests
{
    [Theory]
    [InlineData("80FFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")]
    [InlineData("00FFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")]
    public void SerialRequiringSignPaddingOrTrimmingIsRejectedBeforeSigning(string serial)
    {
        // Arrange
        using EnrollmentCertificateFixture fixture = new();
        EnrollmentIssuance template = fixture.Template with { Serial = serial };

        // Act
        Exception? rejected = Record.Exception(() => EnrollmentCertificates.IssueLeaf(fixture.Issuer,
            fixture.Request, template, fixture.Csr));

        // Assert
        Assert.IsType<InvalidDataException>(rejected);
    }

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

    [Theory]
    [InlineData("spki")]
    [InlineData("csr")]
    [InlineData("node")]
    [InlineData("storage")]
    [InlineData("address")]
    [InlineData("epoch")]
    [InlineData("long-lived")]
    [InlineData("serial")]
    public void IssuanceRejectsForeignIdentityKeyDigestEpochAndTemplate(string mutation)
    {
        // Arrange
        using EnrollmentCertificateFixture fixture = new();
        EnrollmentIssuance template = mutation switch
        {
            "spki" => fixture.Template with { SpkiDigest = new string('F', 64) },
            "csr" => fixture.Template with { CsrDigest = new string('F', 64) },
            "node" => fixture.Template with { NodeId = Guid.NewGuid() },
            "storage" => fixture.Template with { StorageGeneration = Guid.NewGuid() },
            "address" => fixture.Template with { PeerUri = "https://other.example.test/internal/raft" },
            "epoch" => fixture.Template with { IssuerEpoch = Guid.NewGuid() },
            "long-lived" => fixture.Template with { NotAfterUtc = fixture.Now.AddDays(31) },
            "serial" => fixture.Template with { Serial = new string('0', 32) },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };

        // Act
        Exception? failure = Record.Exception(() => EnrollmentCertificates.IssueLeaf(fixture.Issuer, fixture.Request, template, fixture.Csr));

        // Assert
        Assert.IsType<InvalidDataException>(failure);
    }

    [Fact]
    public void CsrSignatureIsVerifiedEvenWhenAttackerSuppliesMatchingDigest()
    {
        // Arrange
        using EnrollmentCertificateFixture fixture = new();
        byte[] corrupted = fixture.Csr.ToArray();
        corrupted[^1] ^= 1;
        EnrollmentRequest request = fixture.Request with { CsrDigest = EnrollmentCertificates.Hash(corrupted) };

        // Act
        Exception? failure = Record.Exception(() => EnrollmentCertificates.ReadCsr(corrupted, request));

        // Assert
        Assert.IsType<InvalidDataException>(failure);
    }

    [Theory]
    [InlineData("ca")]
    [InlineData("san")]
    [InlineData("critical")]
    [InlineData("weak-curve")]
    public void CsrCannotRequestCaOrForeignSanOrCriticalExtensionsOrAnotherCurve(string mutation)
    {
        // Arrange
        using EnrollmentCertificateFixture fixture = new();
        ECCurve curve = ECCurve.NamedCurves.nistP256;
        if (mutation == "weak-curve")
        {
            curve = ECCurve.NamedCurves.nistP384;
        }

        using ECDsa key = ECDsa.Create(curve);
        CertificateRequest csr = new("CN=untrusted", key, HashAlgorithmName.SHA256);
        if (mutation == "ca")
        {
            csr.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 1, true));
        }
        else if (mutation == "san")
        {
            SubjectAlternativeNameBuilder names = new();
            names.AddDnsName("other.example.test");
            csr.CertificateExtensions.Add(names.Build());
        }
        else if (mutation == "critical")
        {
            csr.CertificateExtensions.Add(new X509Extension("1.2.3.4.5", [5, 0], true));
        }

        byte[] encoded = csr.CreateSigningRequest();
        EnrollmentRequest request = fixture.Request with
        {
            SpkiDigest = EnrollmentCertificates.Hash(key.ExportSubjectPublicKeyInfo()), CsrDigest = EnrollmentCertificates.Hash(encoded)
        };

        // Act
        Exception? failure = Record.Exception(() => EnrollmentCertificates.ReadCsr(encoded, request));

        // Assert
        Assert.IsType<InvalidDataException>(failure);
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
