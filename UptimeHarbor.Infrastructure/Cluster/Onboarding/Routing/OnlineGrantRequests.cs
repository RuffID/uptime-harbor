using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;

/// <summary>Сохраняет точный unsigned grant и process proof до удалённой подписи в прежнем локальном журнале.</summary>
public class OnlineGrantRequests(ILocalSetupUnitOfWork work, InstallationState installation, InstallationLease lease,
    LocalSecretStore secrets, ManagedAdmissionState admission, NodeReadiness readiness, RevisionStateStore revisions,
    IOptions<ClusterOptions> options, TimeProvider time, DynamicPeerTls? tls = null)
{
    public async Task<OnlineGrantSigningRequest> PrepareAsync(OnlineGrant grant, CancellationToken token)
    {
        LocalInstallationProfile profile = await work.Profiles.ReadAsync(token)
            ?? throw new InvalidDataException("OnlineGrantRequestProfileMissing");
        LocalInstallationProfile? active = installation.Current;
        if (active is null || !LocalProfileEquality.Same(profile, active) || profile.KeyReference is null
            || profile.BootId != lease.BootId || profile.AttemptId != grant.AttemptId)
        {
            throw new UnauthorizedAccessException("OnlineGrantRequestProfileChanged");
        }

        OnlineGrantLocalSource.Require(grant, options.Value, readiness, admission, lease);
        RevisionImage? image = revisions.Current;
        bool rotated = profile.Mode == "Bootstrap" && image is not null
            && VoterCredentialPolicy.Latest(image, grant.NodeId) is not null;
        if (rotated)
        {
            if (tls?.CaptureWorkingBootstrap(image!, active) is not { } working
                || working.CredentialId != grant.CredentialId || working.Fingerprint != grant.LeafDigest
                || working.IssuerEpoch != grant.IssuerEpoch)
            {
                throw new UnauthorizedAccessException("ManagedRestartCredentialChanged");
            }
        }
        else if (profile.CredentialId != grant.CredentialId)
        {
            throw new UnauthorizedAccessException("OnlineGrantRequestProfileChanged");
        }

        LocalAttemptStep? saved = await work.Profiles.FindStepAsync(OnlineGrantRequestSignatures.IntentId(grant.GrantId), token);
        if (saved is not null)
        {
            OnlineGrantSigningRequest original = OnlineGrantRequestSignatures.RequireStep(profile, saved);
            OnlineGrant previous = original.Grant;
            if (previous with { IssuedAtUtc = grant.IssuedAtUtc, ExpiresAtUtc = grant.ExpiresAtUtc } != grant)
            {
                throw new InvalidDataException("OnlineGrantRequestTemplateChanged");
            }

            OnlineGrantLocalSource.Require(previous, options.Value, readiness, admission, lease);
            RequireProof(original);
            return original;
        }

        OnlineGrantSigningRequest request;
        if (rotated)
        {
            using ECDsa key = tls!.OpenWorkingBootstrapKey(image!, active, grant);
            request = Sign(grant, key);
        }
        else
        {
            byte[] pem = secrets.Read(profile.AttemptId!.Value, "peer", profile.KeyReference);
            try
            {
                using ECDsa key = ECDsa.Create();
                key.ImportFromPem(Encoding.UTF8.GetString(pem));
                request = Sign(grant, key);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pem);
            }
        }

        RequireProof(request);
        byte[] bytes = StrictOnboardingJson.Encode(request);
        LocalAttemptStep step = new(OnlineGrantRequestSignatures.IntentId(grant.GrantId), profile.AttemptId!.Value,
            OnlineGrantRequestSignatures.STEP_KIND, EnrollmentCertificates.Hash(bytes), "SignedOnlineGrant",
            profile.ProfileRevision, profile.RuntimeGeneration, time.GetUtcNow(), Encoding.UTF8.GetString(bytes));
        OnlineGrantRequestSignatures.RequireStep(profile, step);
        await work.ExecuteInTransactionAsync(_ =>
        {
            work.Profiles.AddStep(step);
            return Task.CompletedTask;
        }, token);
        return request;
    }

    private static OnlineGrantSigningRequest Sign(OnlineGrant grant, ECDsa key)
    {
        if (EnrollmentCertificates.Hash(key.ExportSubjectPublicKeyInfo()) != grant.SpkiDigest)
        {
            throw new InvalidDataException("OnlineGrantRequestKeyChanged");
        }

        byte[] signature = key.SignData(OnlineGrantRequestSignatures.Canonical(grant), HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        return new(1, grant, Convert.ToBase64String(signature));
    }

    private void RequireProof(OnlineGrantSigningRequest request)
    {
        if (!revisions.Available || revisions.Current is null
            || !OnlineGrantRequestSignatures.Verify(revisions.Current, request, time.GetUtcNow()))
        {
            throw new UnauthorizedAccessException("OnlineGrantRequestAuthorityChanged");
        }
    }
}
