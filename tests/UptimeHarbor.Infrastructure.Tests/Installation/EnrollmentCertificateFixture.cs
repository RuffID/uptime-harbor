using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

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
    public byte[] Csr { get; }

    public EnrollmentCertificateFixture()
    {
        Csr = new CertificateRequest("CN=untrusted request", PeerKey, HashAlgorithmName.SHA256).CreateSigningRequest();
        Request = new(1, Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "https://peer.example.test/internal/raft", EnrollmentCertificates.Hash(PeerKey.ExportSubjectPublicKeyInfo()),
            EnrollmentCertificates.Hash(Csr));
        Issuer = EnrollmentCertificates.CreateIssuer(IssuerKey, Request.ClusterId, Request.Generation, Epoch, Now);
        Template = new(1, Guid.NewGuid(), Epoch, "0123456789ABCDEF0123456789ABCDEF", Request.NodeId,
            Request.StorageGeneration, Request.PeerUri, Request.SpkiDigest, Request.CsrDigest, Now, Now.AddDays(30));
    }

    public X509Certificate2 Issue()
    {
        return EnrollmentCertificates.IssueLeaf(Issuer, Request, Template, Csr);
    }

    public void Dispose()
    {
        Issuer.Dispose();
        IssuerKey.Dispose();
        PeerKey.Dispose();
    }
}
