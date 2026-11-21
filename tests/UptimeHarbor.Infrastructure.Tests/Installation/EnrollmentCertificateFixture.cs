using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class EnrollmentCertificateFixture : IDisposable
{
    public DateTimeOffset Now { get; } = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
    public ECDsa IssuerKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public ECDsa PeerKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public Guid Epoch { get; } = Guid.NewGuid();
    public X509Certificate2 Issuer { get; }
    public EnrollmentRequest Request { get; }
    public EnrollmentIssuance Template { get; }

    public EnrollmentCertificateFixture()
    {
        byte[] csr = new CertificateRequest("CN=untrusted request", PeerKey, HashAlgorithmName.SHA256).CreateSigningRequest();
        Request = new(1, Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "https://peer.example.test/internal/raft", EnrollmentCertificates.Hash(PeerKey.ExportSubjectPublicKeyInfo()),
            EnrollmentCertificates.Hash(csr));
        CertificateRequest issuer = new($"CN=UptimeHarbor Enrollment {Epoch:D}", IssuerKey, HashAlgorithmName.SHA256);
        issuer.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        issuer.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        SubjectAlternativeNameBuilder issuerNames = new();
        issuerNames.AddUri(new Uri($"urn:uptimeharbor:issuer:{Request.ClusterId:D}:{Request.Generation}:{Epoch:D}:{EnrollmentIssuer.PEER_PURPOSE}"));
        issuer.CertificateExtensions.Add(issuerNames.Build());
        Issuer = issuer.CreateSelfSigned(Now, Now.AddYears(3));
        Template = new(1, Guid.NewGuid(), Epoch, "0123456789ABCDEF0123456789ABCDEF", Request.NodeId,
            Request.StorageGeneration, Request.PeerUri, Request.SpkiDigest, Request.CsrDigest, Now, Now.AddDays(30));
    }

    public X509Certificate2 Issue()
    {
        return Issue(PeerKey, Template);
    }

    public X509Certificate2 Issue(ECDsa key, EnrollmentIssuance template)
    {
        CertificateRequest leaf = new($"CN=UptimeHarbor {Request.NodeId:D}", key, HashAlgorithmName.SHA256);
        leaf.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leaf.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        leaf.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.2"), new("1.3.6.1.5.5.7.3.1") }, true));
        SubjectAlternativeNameBuilder names = new();
        names.AddUri(new Uri($"urn:uptimeharbor:{Request.ClusterId:D}:{Request.Generation}:{Request.NodeId:D}"));
        names.AddUri(new Uri($"urn:uptimeharbor:storage:{Request.StorageGeneration:D}:credential:{template.IssueId:D}"));
        names.AddDnsName(new Uri(template.PeerUri).IdnHost);
        leaf.CertificateExtensions.Add(names.Build());

        return leaf.Create(Issuer, template.NotBeforeUtc, template.NotAfterUtc, Convert.FromHexString(template.Serial));
    }

    public void Dispose()
    {
        Issuer.Dispose();
        IssuerKey.Dispose();
        PeerKey.Dispose();
    }
}
