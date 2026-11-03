using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Подписывает только purpose-bound grant после свежей authority; сохранение request до отправки — у coordinator.</summary>
public class OnlineGrantSigner(IEnrollmentSigningRepository signing, IOnlineGrantSigningAuthorization authority,
    EnrollmentIssuerSigningLock issuerLock, LocalSecretStore secrets, InstallationLease lease, TimeProvider time)
{
    public async Task<OnlineGrant> SignAsync(OnlineGrant grant, CancellationToken token)
    {
        OnlineLifecycleCommands.ValidateGrant(grant);
        if (grant.Signature.Length != 0 || grant.Purpose == OnlineGrantPurpose.CatchUp)
        {
            // Catch-up grant требует snapshot-bound authority этапа 05. Общий signer её не подменяет.
            throw new InvalidDataException("OnlineGrantSigningPurposeUnavailable");
        }

        await issuerLock.Semaphore.WaitAsync(token);
        try
        {
            EnrollmentIssuerReference localIssuer = await signing.FindIssuerAsync(grant.IssuerEpoch, token)
                ?? throw new InvalidDataException("LocalEnrollmentIssuerMissing");
            if (localIssuer.InstallIdentity != lease.InstallIdentity || localIssuer.ClusterId != grant.ClusterId
                || localIssuer.Generation != grant.Generation || localIssuer.CertificateDigest != EnrollmentCertificates.Hash(localIssuer.CertificateDer))
            {
                throw new InvalidDataException("OnlineGrantIssuerBindingInvalid");
            }

            await authority.ConfirmAsync(grant, localIssuer, token);
            using X509Certificate2 root = X509CertificateLoader.LoadCertificate(localIssuer.CertificateDer);
            if (!EnrollmentCertificates.ValidateIssuer(root, grant.ClusterId, grant.Generation, grant.IssuerEpoch, time.GetUtcNow()))
            {
                throw new InvalidDataException("OnlineGrantIssuerInvalid");
            }

            byte[] pem = secrets.Read(localIssuer.Epoch, "issuer", localIssuer.KeyReference);
            try
            {
                using ECDsa key = ECDsa.Create();
                key.ImportFromPem(Encoding.UTF8.GetString(pem));
                if (EnrollmentCertificates.Hash(key.ExportSubjectPublicKeyInfo()) != localIssuer.SpkiDigest
                    || localIssuer.SpkiDigest != EnrollmentCertificates.Hash(root.PublicKey.ExportSubjectPublicKeyInfo()))
                {
                    throw new InvalidDataException("OnlineGrantIssuerKeyInvalid");
                }

                byte[] signature = key.SignData(OnlineLifecycleCommands.Canonical(grant), HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence);
                return grant with { Signature = Convert.ToBase64String(signature) };
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
