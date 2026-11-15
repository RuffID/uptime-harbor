using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Issuers;

/// <summary>Локальный ключ и SQL ref сохраняются до отправки публичного Register; не являются authority.</summary>
public class EnrollmentIssuerProvisioning(IEnrollmentSigningUnitOfWork work, EnrollmentSigningJournal journal,
    EnrollmentIssuerStorage storage, EnrollmentIssuerSigningLock issuerLock, IOptions<ClusterOptions> options,
    IOptions<InstallationOptions> installation, TimeProvider time)
{
    public async Task<EnrollmentIssuerReference> ReadAsync(Guid epoch, CancellationToken token)
    {
        EnrollmentIssuerReference existing = await work.Signing.FindIssuerAsync(epoch, token)
            ?? throw new InvalidDataException("LocalEnrollmentIssuerMissing");
        (InstallationLease lease, LocalSecretStore secrets) = storage.Open(create: false);

        return await journal.SaveIssuerOnceAsync(existing, secrets, lease, time.GetUtcNow(), token);
    }

    public async Task<EnrollmentIssuerReference> PrepareAsync(Guid operationId, CancellationToken token)
    {
        if (installation.Value.InstallationIntent != "Legacy")
        {
            throw new InvalidOperationException("ManualIssuerRequired");
        }

        await issuerLock.Semaphore.WaitAsync(token);
        try
        {
            IReadOnlyList<EnrollmentIssuerReference> all = await work.Signing.ListIssuersAsync(token);
            EnrollmentIssuerReference? existing = all.SingleOrDefault(item => item.Epoch == operationId);
            if (existing is null && all.Count != 0)
            {
                throw new InvalidOperationException("IssuerProvisioningAlreadyPrepared");
            }

            (InstallationLease lease, LocalSecretStore secrets) = storage.Open(create: true);
            ClusterOptions current = options.Value;
            if (existing is not null)
            {
                if (existing.ClusterId != current.ClusterId || existing.Generation != current.Generation
                    || existing.NodeId != current.NodeId || existing.StorageGeneration != current.StorageGeneration)
                {
                    throw new InvalidDataException("IssuerProvisioningIdentityChanged");
                }

                return await journal.SaveIssuerOnceAsync(existing, secrets, lease, time.GetUtcNow(), token);
            }

            // Не выбирать orphan-файлы после неизвестного сохранения предыдущего нового ключа.
            secrets.ReconcileAll(null);

            DateTimeOffset now = EnrollmentCertificates.Seconds(time.GetUtcNow());
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using X509Certificate2 certificate = EnrollmentCertificates.CreateIssuer(key, current.ClusterId,
                current.Generation, operationId, now);
            byte[] pem = Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem());
            string reference;
            try
            {
                reference = secrets.Write(operationId, "issuer", pem);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pem);
            }

            EnrollmentIssuerReference issuer = new(operationId, lease.InstallIdentity, current.ClusterId,
                current.Generation, current.NodeId, current.StorageGeneration, reference, certificate.RawData,
                EnrollmentCertificates.Hash(certificate.RawData), EnrollmentCertificates.Hash(key.ExportSubjectPublicKeyInfo()));
            return await journal.SaveIssuerOnceAsync(issuer, secrets, lease, now, token);
        }
        finally
        {
            issuerLock.Semaphore.Release();
        }
    }

}
