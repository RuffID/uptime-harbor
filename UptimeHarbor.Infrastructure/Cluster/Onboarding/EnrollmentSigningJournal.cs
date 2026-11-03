using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Согласование immutable локальных SQL references. Запись в журнал не предоставляет authority.</summary>
public class EnrollmentSigningJournal(IEnrollmentSigningUnitOfWork work, EnrollmentSigningLock signingLock)
{
    public async Task<EnrollmentIssuerReference> SaveIssuerOnceAsync(EnrollmentIssuerReference issuer,
        LocalSecretStore secrets, InstallationLease lease, DateTimeOffset now, CancellationToken token)
    {
        if (issuer.InstallIdentity != lease.InstallIdentity || issuer.CertificateDer.Length is < 1 or > EnrollmentCertificates.MAX_DER_BYTES
            || issuer.CertificateDigest != EnrollmentCertificates.Hash(issuer.CertificateDer))
        {
            throw new InvalidDataException("LocalEnrollmentIssuerIdentityMismatch");
        }

        using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(issuer.CertificateDer);
        if (!EnrollmentCertificates.ValidateIssuer(certificate, issuer.ClusterId, issuer.Generation, issuer.Epoch, now)
            || issuer.SpkiDigest != EnrollmentCertificates.Hash(certificate.PublicKey.ExportSubjectPublicKeyInfo()))
        {
            throw new InvalidDataException("LocalEnrollmentIssuerCertificateInvalid");
        }

        // Проверка key reference остаётся обязанностью существующего AES-GCM хранилища.
        byte[] pem = secrets.Read(issuer.Epoch, "issuer", issuer.KeyReference);
        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportFromPem(Encoding.UTF8.GetString(pem));
            if (issuer.SpkiDigest != EnrollmentCertificates.Hash(key.ExportSubjectPublicKeyInfo()))
            {
                throw new InvalidDataException("LocalEnrollmentIssuerKeyMismatch");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pem);
        }

        await signingLock.Semaphore.WaitAsync(token);
        try
        {
            EnrollmentIssuerReference? existing = await work.Signing.FindIssuerAsync(issuer.Epoch, token);
            if (existing is not null)
            {
                bool same = existing with { CertificateDer = issuer.CertificateDer } == issuer
                    && existing.CertificateDer.AsSpan().SequenceEqual(issuer.CertificateDer);
                if (!same)
                {
                    throw new InvalidDataException("LocalEnrollmentIssuerChanged");
                }

                return existing with { CertificateDer = existing.CertificateDer.ToArray() };
            }

            work.Signing.AddIssuer(issuer);
            await work.SaveChangesAsync(token);
            return issuer with { CertificateDer = issuer.CertificateDer.ToArray() };
        }
        finally
        {
            signingLock.Semaphore.Release();
        }
    }

    public async Task<EnrollmentIssuedArtifact> SaveIssueOnceAsync(EnrollmentIssuedArtifact artifact,
        EnrollmentRequest request, EnrollmentIssuance template, DateTimeOffset now, CancellationToken token)
    {
        ValidateArtifact(artifact, request, template);
        await signingLock.Semaphore.WaitAsync(token);
        try
        {
            EnrollmentIssuerReference issuer = await work.Signing.FindIssuerAsync(artifact.IssuerEpoch, token)
                ?? throw new InvalidDataException("LocalEnrollmentIssuerMissing");
            using X509Certificate2 root = X509CertificateLoader.LoadCertificate(issuer.CertificateDer);
            if (issuer.ClusterId != request.ClusterId || issuer.Generation != request.Generation
                || issuer.CertificateDigest != EnrollmentCertificates.Hash(issuer.CertificateDer))
            {
                throw new InvalidDataException("LocalEnrollmentIssuerIdentityMismatch");
            }

            EnrollmentIssuedArtifact? existing = await work.Signing.FindIssueAsync(artifact.IssueId, token);
            if (existing is not null)
            {
                ValidateArtifact(existing, request, template);
                bool same = existing.IssuerEpoch == artifact.IssuerEpoch && existing.AttemptId == artifact.AttemptId
                    && existing.RequestDigest == artifact.RequestDigest && existing.TemplateDigest == artifact.TemplateDigest
                    && existing.GenesisId == artifact.GenesisId && existing.AuthorizationIndex == artifact.AuthorizationIndex;
                if (!same)
                {
                    throw new InvalidDataException("LocalEnrollmentIssueIdentityChanged");
                }

                ValidateCertificate(existing, root, request, template, now);
                return existing with { CertificateDer = existing.CertificateDer.ToArray() };
            }

            ValidateCertificate(artifact, root, request, template, now);
            work.Signing.AddIssue(artifact);
            // До завершения этого commit DER не возвращаются во внешний ответ.
            await work.SaveChangesAsync(token);
            return artifact with { CertificateDer = artifact.CertificateDer.ToArray() };
        }
        finally
        {
            signingLock.Semaphore.Release();
        }
    }

    public static string TemplateDigest(EnrollmentIssuance template)
    {
        return EnrollmentCertificates.Hash(StrictOnboardingJson.Encode(template));
    }

    private static void ValidateArtifact(EnrollmentIssuedArtifact artifact, EnrollmentRequest request, EnrollmentIssuance template)
    {
        EnrollmentCertificates.ValidateTemplate(request, template);
        bool authorization = artifact.AuthorizationIndex > 0 && artifact.GenesisId is null;
        if (artifact.GenesisId.HasValue)
        {
            authorization = artifact.AuthorizationIndex == 0 && artifact.GenesisId != Guid.Empty;
        }

        if (!authorization || artifact.IssueId != template.IssueId || artifact.IssuerEpoch != template.IssuerEpoch
            || artifact.AttemptId != request.AttemptId || artifact.RequestDigest != EnrollmentRequestCodec.Digest(request)
            || artifact.TemplateDigest != TemplateDigest(template) || artifact.CertificateDer.Length is < 1 or > EnrollmentCertificates.MAX_DER_BYTES
            || artifact.CertificateDigest != EnrollmentCertificates.Hash(artifact.CertificateDer))
        {
            throw new InvalidDataException("LocalEnrollmentIssueInvalid");
        }
    }

    private static void ValidateCertificate(EnrollmentIssuedArtifact artifact, X509Certificate2 root,
        EnrollmentRequest request, EnrollmentIssuance template, DateTimeOffset now)
    {
        using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(artifact.CertificateDer);
        if (!EnrollmentCertificates.ValidateLeaf(leaf, root, request, template, artifact.CertificateDigest, now, true))
        {
            throw new InvalidDataException("LocalEnrollmentIssueCertificateInvalid");
        }
    }
}
