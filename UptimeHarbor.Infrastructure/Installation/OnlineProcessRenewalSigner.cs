using System.Security.Cryptography;
using System.Text;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Продлевает только действующий процесс прежним peer key. Не выдаёт grant или новый ProcessEpoch.</summary>
public class OnlineProcessRenewalSigner(ILocalSetupUnitOfWork work, InstallationState installation,
    InstallationLease lease, RevisionStateStore revisions, LocalSecretStore secrets, TimeProvider time)
{
    public async Task<OnlineGrant> SignAsync(OnlineGrant request, CancellationToken token)
    {
        OnlineLifecycleCommands.ValidateGrant(request);
        LocalInstallationProfile? profile = await work.Profiles.ReadAsync(token);
        RevisionImage? image = revisions.Current;
        bool localProcess = profile is not null && LocalProfileEquality.Same(profile, installation.Current)
            && profile.BootId == lease.BootId && request.BootId == lease.BootId && request.BootChallenge == lease.BootChallenge
            && profile.ClusterId == request.ClusterId && profile.ClusterGeneration == request.Generation
            && profile.NodeId == request.NodeId && profile.StorageGeneration == request.StorageGeneration
            && profile.AttemptId == request.AttemptId && profile.CredentialId == request.CredentialId
            && profile.PeerAddress == request.PeerUri && profile.KeyReference is not null;
        if (!localProcess || request.Purpose != OnlineGrantPurpose.Process || request.Signature.Length != 0
            || !revisions.Available || image is null || !OnlineGrantPolicy.Current(image, request, time.GetUtcNow(), processRenewal: true))
        {
            throw new UnauthorizedAccessException("OnlineProcessRenewalUnconfirmed");
        }

        byte[] pem = secrets.Read(profile!.AttemptId!.Value, "peer", profile.KeyReference!);
        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportFromPem(Encoding.UTF8.GetString(pem));
            if (EnrollmentCertificates.Hash(key.ExportSubjectPublicKeyInfo()) != request.SpkiDigest)
            {
                throw new InvalidDataException("OnlineProcessRenewalKeyChanged");
            }

            byte[] signature = key.SignData(OnlineLifecycleCommands.Canonical(request, processRenewal: true),
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            return request with { Signature = Convert.ToBase64String(signature) };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pem);
        }
    }
}
