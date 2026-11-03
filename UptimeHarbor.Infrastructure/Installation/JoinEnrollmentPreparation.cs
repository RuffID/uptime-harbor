using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Discovery не передаёт invitation; exact origin/CSR/request сохраняются до первого reserve.</summary>
public class JoinEnrollmentPreparation(IOnboardingProgressUnitOfWork work, InstallationState installation,
    LocalSecretStore secrets, OnboardingProgress progress, EnrollmentTransport transport,
    EnrollmentEndpoints endpoints)
{
    public async Task<LocalOnboardingAttempt> PrepareAsync(string browserOrigin, string? invitation, CancellationToken token)
    {
        await installation.Transition.WaitAsync(token);
        try
        {
            LocalInstallationProfile profile = installation.Current ?? throw new InvalidOperationException("InstallationProfileMissing");
            if (profile.Mode != "Join" || profile.Phase != InstallationPhase.LocalPrepared || profile.AttemptId is null)
            {
                throw new InvalidOperationException("EnrollmentLocalPreparationRequired");
            }

            Uri origin = endpoints.Origin(browserOrigin);
            LocalOnboardingAttempt? existing = await work.Attempts.FindAsync(profile.AttemptId.Value, token);
            if (existing is not null)
            {
                if (existing.BrowserOrigin != origin.AbsoluteUri || existing.CsrDer is null)
                {
                    throw new InvalidDataException("EnrollmentOriginOrIntentChanged");
                }

                return await progress.PrepareAsync(existing, token);
            }

            Guid code = Guid.ParseExact(invitation ?? throw new InvalidDataException("EnrollmentInvitationRequired"), "D");
            EnrollmentDiscovery discovery = await transport.DiscoverAsync<EnrollmentDiscovery>(origin, token);
            EnrollmentDescriptorValidation.Discovery(discovery, origin, endpoints);
            // Проверка UUID version/variant до защиты и передачи секрета.
            InvitationSecrets.Verifier(discovery.ClusterId, discovery.Generation, discovery.InvitationId, code);
            byte[] pem = secrets.Read(profile.AttemptId.Value, "peer", profile.KeyReference!);
            try
            {
                using ECDsa key = ECDsa.Create();
                key.ImportFromPem(Encoding.UTF8.GetString(pem));
                byte[] csr = new CertificateRequest("CN=UptimeHarbor enrollment", key, HashAlgorithmName.SHA256).CreateSigningRequest();
                EnrollmentRequest request = new(1, discovery.ClusterId, discovery.Generation, discovery.InvitationId,
                    profile.AttemptId.Value, profile.NodeId!.Value, profile.StorageGeneration!.Value, profile.PeerAddress!,
                    EnrollmentCertificates.Hash(key.ExportSubjectPublicKeyInfo()), EnrollmentCertificates.Hash(csr));
                Dictionary<string, Guid> operations = new(StringComparer.Ordinal)
                {
                    ["ReserveInvitation"] = profile.OperationIds!["reserve"], ["AdmitInvitedNode"] = profile.OperationIds["admit"]
                };
                foreach (string action in new[] { "AuthorizeEnrollmentCertificate", "BindEnrollmentCredential" })
                {
                    operations[action] = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"EnrollmentOperation/v1/{profile.AttemptId:D}/{action}")).AsSpan(0, 16));
                }

                byte[] plaintext = Encoding.UTF8.GetBytes(code.ToString("D"));
                string protectedInvitation;
                try
                {
                    protectedInvitation = secrets.ProtectInvitation(profile.AttemptId.Value, plaintext);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }

                LocalOnboardingAttempt local = new(profile.AttemptId.Value, request, EnrollmentRequestCodec.Digest(request),
                    profile.KeyReference!, 1, operations.ToImmutableDictionary(), protectedInvitation, null)
                {
                    BrowserOrigin = origin.AbsoluteUri, CsrDer = Convert.ToBase64String(csr)
                };
                return await progress.PrepareAsync(local, token);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pem);
            }
        }
        finally
        {
            installation.Transition.Release();
        }
    }
}
