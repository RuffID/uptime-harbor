using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Installation;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;

/// <summary>Отдельный domain peer-key proof; эта подпись не является issuer grant или process renewal.</summary>
public static class OnlineGrantRequestSignatures
{
    public const string STEP_KIND = "OnlineGrantRequest";

    public static byte[] Canonical(OnlineGrant grant)
    {
        OnlineLifecycleCommands.ValidateGrant(grant);
        if (grant.Signature.Length != 0 || grant.Purpose == OnlineGrantPurpose.CatchUp || grant.SnapshotDigest is not null)
        {
            throw new InvalidDataException("OnlineGrantRequestPurposeInvalid");
        }

        return StrictOnboardingJson.Encode(new { Domain = "UptimeHarbor.OnlineGrantRequest.v1", Grant = grant });
    }

    public static Guid IntentId(Guid grantId)
    {
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"UptimeHarbor.OnlineGrantRequest.Intent.v1/{grantId:D}")).AsSpan(0, 16));
    }

    public static bool Verify(RevisionImage image, OnlineGrantSigningRequest request, DateTimeOffset now)
    {
        try
        {
            OnlineGrant grant = request.Grant;
            if (request.Version != 1 || request.ProcessProof.Length is < 12 or > 112
                || !OnlineGrantPolicy.Current(image, grant, now, grant.Purpose == OnlineGrantPurpose.Process))
            {
                return false;
            }

            OperationalCredential? credential = image.Operations.Credentials.GetValueOrDefault(grant.CredentialId);
            if (credential is null || credential.NodeId != grant.NodeId || credential.Fingerprint != grant.LeafDigest)
            {
                return false;
            }

            using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(credential.PublicCertificate));
            using ECDsa? key = leaf.GetECDsaPublicKey();
            return key is not null && EnrollmentLiveTrust.Validate(image, leaf, now)
                && EnrollmentCertificates.Hash(leaf.PublicKey.ExportSubjectPublicKeyInfo()) == grant.SpkiDigest
                && key.VerifyData(Canonical(grant), Convert.FromBase64String(request.ProcessProof),
                    HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception error) when (error is CryptographicException or FormatException or InvalidDataException or ArgumentException)
        {
            return false;
        }
    }

    public static OnlineGrantSigningRequest RequireStep(LocalInstallationProfile profile, LocalAttemptStep step)
    {
        OnlineGrantSigningRequest request = StrictOnboardingJson.Decode<OnlineGrantSigningRequest>(Encoding.UTF8.GetBytes(step.Evidence
            ?? throw new InvalidDataException("OnlineGrantRequestIntentMissing")));
        OnlineGrant grant = request.Grant;
        Canonical(grant);
        bool identity = grant.AttemptId == profile.AttemptId && grant.ClusterId == profile.ClusterId
            && grant.Generation == profile.ClusterGeneration && grant.NodeId == profile.NodeId
            && grant.StorageGeneration == profile.StorageGeneration && grant.CredentialId == profile.CredentialId
            && grant.PeerUri == profile.PeerAddress;
        bool intent = step.Kind == STEP_KIND && step.ExpectedResult == "SignedOnlineGrant" && request.Version == 1
            && step.OperationId == IntentId(grant.GrantId) && step.AttemptId == profile.AttemptId
            && step.ProfileRevision <= profile.ProfileRevision && step.RuntimeGeneration <= profile.RuntimeGeneration
            && step.RequestDigest == EnrollmentCertificates.Hash(StrictOnboardingJson.Encode(request))
            && request.ProcessProof.Length is >= 12 and <= 112;
        if (!identity || !intent)
        {
            throw new InvalidDataException("OnlineGrantRequestIntentChanged");
        }

        return request;
    }
}
