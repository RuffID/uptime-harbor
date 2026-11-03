using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Только exact accepted leaf. DER возвращаются после локального durable commit, не до него.</summary>
public class EnrollmentSigner(IEnrollmentSigningUnitOfWork work, EnrollmentSigningJournal journal,
    IEnrollmentSigningAuthorization authorization, EnrollmentIssuerSigningLock issuerLock,
    InstallationLease lease, LocalSecretStore secrets, TimeProvider time, ILogger<EnrollmentSigner> logger)
{
    public async Task<EnrollmentIssuedArtifact> IssueAsync(EnrollmentRequest request, EnrollmentIssuance template,
        byte[] csr, CancellationToken token)
    {
        EnrollmentCertificates.ValidateTemplate(request, template);
        EnrollmentCertificates.ReadCsr(csr, request);
        await issuerLock.Semaphore.WaitAsync(token);
        try
        {
            EnrollmentIssuerReference local = await work.Signing.FindIssuerAsync(template.IssuerEpoch, token)
                ?? throw new InvalidDataException("LocalEnrollmentIssuerMissing");
            if (local.InstallIdentity != lease.InstallIdentity || local.ClusterId != request.ClusterId || local.Generation != request.Generation)
            {
                throw new InvalidDataException("LocalEnrollmentIssuerIdentityMismatch");
            }

            // Проверка выполняется внутри issuer lock. Никакого локального fallback при отсутствии majority.
            long index = await authorization.ConfirmAsync(request, template, local, token);
            if (index <= 0)
            {
                throw new UnauthorizedAccessException("EnrollmentIssuanceNotAuthorized");
            }

            EnrollmentIssuedArtifact? existing = await work.Signing.FindIssueAsync(template.IssueId, token);
            if (existing is not null)
            {
                if (existing.AuthorizationIndex != index || existing.GenesisId.HasValue)
                {
                    throw new InvalidDataException("LocalEnrollmentIssueAuthorizationChanged");
                }

                return await journal.SaveIssueOnceAsync(existing, request, template, time.GetUtcNow(), token);
            }

            byte[] pem = secrets.Read(local.Epoch, "issuer", local.KeyReference);
            try
            {
                using ECDsa key = ECDsa.Create();
                key.ImportFromPem(Encoding.UTF8.GetString(pem));
                using X509Certificate2 publicIssuer = X509CertificateLoader.LoadCertificate(local.CertificateDer);
                if (local.CertificateDigest != EnrollmentCertificates.Hash(publicIssuer.RawData)
                    || local.SpkiDigest != EnrollmentCertificates.Hash(key.ExportSubjectPublicKeyInfo())
                    || local.SpkiDigest != EnrollmentCertificates.Hash(publicIssuer.PublicKey.ExportSubjectPublicKeyInfo()))
                {
                    throw new InvalidDataException("LocalEnrollmentIssuerKeyMismatch");
                }

                using X509Certificate2 issuer = publicIssuer.CopyWithPrivateKey(key);
                using X509Certificate2 leaf = EnrollmentCertificates.IssueLeaf(issuer, request, template, csr);
                EnrollmentIssuedArtifact artifact = new(template.IssueId, template.IssuerEpoch, request.AttemptId,
                    EnrollmentRequestCodec.Digest(request), EnrollmentSigningJournal.TemplateDigest(template), null,
                    index, leaf.RawData, EnrollmentCertificates.Hash(leaf.RawData));
                EnrollmentIssuedArtifact saved = await journal.SaveIssueOnceAsync(artifact, request, template, time.GetUtcNow(), token);
                logger.LogInformation("Enrollment certificate persisted {IssueId} {AttemptId} {IssuerEpoch} at {AuthorizationIndex}.",
                    saved.IssueId, saved.AttemptId, saved.IssuerEpoch, saved.AuthorizationIndex);
                return saved;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pem);
            }
        }
        finally
        {
            issuerLock.Semaphore.Release();
        }
    }
}
