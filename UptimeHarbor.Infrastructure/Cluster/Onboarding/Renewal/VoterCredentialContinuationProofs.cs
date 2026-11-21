using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

/// <summary>Доказательство для нового HTTPS-запроса по managed leaf. Вызывается до принятия команды.</summary>
internal static class VoterCredentialContinuationProofs
{
    internal const string SCOPE = "ContinueCredential";

    internal static VoterCredentialProof Sign(VoterCredentialRequest request, VoterCredentialProof proof, ECDsa previousKey)
    {
        return VoterCredentialProofs.Sign(request, proof, previousKey, Canonical(request, proof));
    }

    internal static AcceptedEnrollmentIssuer Require(RevisionImage image, VoterCredentialProofRequest call, DateTimeOffset now)
    {
        return Require(image, call, now, Canonical(call.Request, call.Proof));
    }

    internal static AcceptedEnrollmentIssuer RequireChallenge(RevisionImage image, VoterCredentialProofRequest call,
        DateTimeOffset now, string purpose, string audience, string? authorizationDigest)
    {
        return Require(image, call, now, Challenge(call.Request, call.Proof, purpose, audience, authorizationDigest));
    }

    internal static VoterCredentialProof SignChallenge(VoterCredentialRequest request, VoterCredentialProof proof, ECDsa previousKey,
        string purpose, string audience, string? authorizationDigest)
    {
        return VoterCredentialProofs.Sign(request, proof, previousKey, Challenge(request, proof, purpose, audience, authorizationDigest));
    }

    internal static AcceptedEnrollmentIssuer RequireBoundDiscovery(RevisionImage image, VoterCredentialProofRequest call,
        DateTimeOffset now, string purpose, string audience, string queryDigest)
    {
        return Require(image, call, now, Challenge(call.Request, call.Proof, purpose, audience, queryDigest),
            checkProcess: false);
    }

    private static byte[] Challenge(VoterCredentialRequest request, VoterCredentialProof proof, string purpose,
        string audience, string? authorizationDigest)
    {
        return StrictOnboardingJson.Encode(new
        {
            Domain = "UptimeHarbor.VoterCredential.ContinuationChallenge.v1", Purpose = purpose, Audience = audience,
            AuthorizationDigest = authorizationDigest, Proof = Convert.ToBase64String(Canonical(request, proof))
        });
    }

    private static AcceptedEnrollmentIssuer Require(RevisionImage image, VoterCredentialProofRequest call, DateTimeOffset now,
        byte[] canonical, bool checkProcess = true)
    {
        StorageGuards.Utc(now);
        VoterCredentialRequest request = call.Request;
        VoterCredentialProof proof = call.Proof;
        EnrollmentIssuance leaf = request.Renewal.Issuance;
        OperationalNode node = EnrollmentPeerAuthority.RequireVoter(image, leaf.NodeId, leaf.StorageGeneration);
        if (image.ClusterId != request.ClusterId || image.Generation != request.Generation || node.Address != leaf.PeerUri
            || proof.MembershipVersion != image.Operations.MembershipVersion || proof.IssuedAtUtc > now
            || now - proof.IssuedAtUtc >= TimeSpan.FromSeconds(60))
        {
            throw new UnauthorizedAccessException("VoterCredentialContinuationContextInvalid");
        }

        if (checkProcess)
        {
            RequireProcessCandidate(image, proof, now);
        }

        if (request.BoundSource is not null)
        {
            if (!VoterContinuationBoundRecovery.TryResolveBind(image, request, out RevisionCommand? bind)
                || !VoterContinuationBinding.IsCurrentBinding(image, bind))
            {
                throw new UnauthorizedAccessException("VoterContinuationBoundSourceChanged");
            }

            image = VoterContinuationBinding.BeforeBinding(image, leaf.IssueId);
        }

        VoterCredentialRenewal renewal = request.Renewal;
        OperationalCredential? previous = image.Operations.Credentials.GetValueOrDefault(renewal.PreviousCredentialId);
        VoterCredentialRotation? latest = VoterCredentialPolicy.Latest(image, node.NodeId);
        bool latestMatches = latest is null || latest.Renewal.Issuance.IssueId == renewal.PreviousCredentialId
            && latest.Renewal.Issuance.SpkiDigest == renewal.PreviousSpkiDigest;
        if (previous is null || previous.NodeId != node.NodeId || previous.Fingerprint != renewal.PreviousFingerprint
            || previous.RevokedAtUtc.HasValue || now < previous.ExpiresAtUtc
            || !latestMatches || renewal.PreviousCredentialRevision != (latest?.CredentialRevision ?? 0))
        {
            throw new UnauthorizedAccessException("VoterCredentialContinuationLeafUnavailable");
        }

        using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(previous.PublicCertificate));
        using ECDsa? key = certificate.GetECDsaPublicKey();
        DateTimeOffset expiresAt = new(certificate.NotAfter.ToUniversalTime());

        // Историческая проверка DER использует текущий registry и текущие отметки отзыва.
        // Срок годности для TLS и peer admission определяется их обычными live-проверками.
        if (expiresAt != previous.ExpiresAtUtc || key is null || key.KeySize != 256
            || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value
            || EnrollmentCertificates.Hash(key.ExportSubjectPublicKeyInfo()) != renewal.PreviousSpkiDigest
            || !VoterCredentialLeafTrust.RequiresDedicatedTrust(image, previous, certificate)
            || !VoterCredentialLeafTrust.ValidatePrevious(image, previous, certificate, expiresAt.AddSeconds(-1), legacy: null))
        {
            throw new UnauthorizedAccessException("VoterCredentialContinuationLeafInvalid");
        }

        if (proof.Signature.Length is < 12 or > 112)
        {
            throw new UnauthorizedAccessException("VoterCredentialContinuationProofInvalid");
        }

        byte[] signature = Convert.FromBase64String(proof.Signature);
        if (Convert.ToBase64String(signature) != proof.Signature
            || !key.VerifyData(canonical, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
        {
            throw new UnauthorizedAccessException("VoterCredentialContinuationProofInvalid");
        }

        byte[] csr = VoterCredentialProofs.ReadCsr(call);
        EnrollmentCertificates.ReadCsr(csr, VoterCredentialProofs.CertificateIdentity(request));
        return RequirePreviousIssuer(image, previous.Id, certificate);
    }

    internal static AcceptedEnrollmentIssuer ReadPreviousIssuer(RevisionImage image, Guid credentialId)
    {
        OperationalCredential credential = image.Operations.Credentials.GetValueOrDefault(credentialId)
            ?? throw new UnauthorizedAccessException("VoterCredentialContinuationLeafUnavailable");
        using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(credential.PublicCertificate));

        return RequirePreviousIssuer(image, credentialId, certificate);
    }

    private static AcceptedEnrollmentIssuer RequirePreviousIssuer(RevisionImage image, Guid credentialId, X509Certificate2 certificate)
    {
        Guid? epoch = image.OnboardingContracts?.VoterCredentialRotations?.GetValueOrDefault(credentialId)?.Renewal.Issuance.IssuerEpoch;
        AcceptedEnrollmentIssuer? matched = null;
        foreach (AcceptedEnrollmentIssuer issuer in image.OnboardingContracts?.Issuers?.Values ?? [])
        {
            if (epoch.HasValue && issuer.Epoch != epoch)
            {
                continue;
            }

            using X509Certificate2 root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(issuer.CertificateDer));
            if (!certificate.IssuerName.RawData.AsSpan().SequenceEqual(root.SubjectName.RawData))
            {
                continue;
            }

            if (matched is not null)
            {
                throw new UnauthorizedAccessException("VoterContinuationPreviousIssuerAmbiguous");
            }

            matched = issuer;
        }

        return matched ?? throw new UnauthorizedAccessException("VoterContinuationPreviousIssuerUnavailable");
    }

    private static void RequireProcessCandidate(RevisionImage image, VoterCredentialProof proof, DateTimeOffset now)
    {
        OnlineProcessLease? previous = image.OnboardingContracts?.OnlineProcesses?.GetValueOrDefault(proof.OriginNodeId);
        VoterContinuationProcess process = new(proof.BootId, proof.BootChallenge, proof.ProcessEpoch, proof.RecoveryVersion);
        if (!VoterContinuationReducer.ProcessCandidate(previous, process, now))
        {
            throw new UnauthorizedAccessException("VoterCredentialContinuationProcessUnavailable");
        }
    }

    private static byte[] Canonical(VoterCredentialRequest request, VoterCredentialProof proof)
    {
        string requestDigest = VoterCredentialProofs.Digest(request);
        StorageGuards.Utc(proof.IssuedAtUtc);
        StorageGuards.Id(proof.BootId, nameof(proof.BootId));
        EnrollmentIssuance leaf = request.Renewal.Issuance;
        if (proof.Version != 1 || proof.Scope != SCOPE || proof.OriginNodeId != leaf.NodeId
            || proof.OriginStorageGeneration != leaf.StorageGeneration || proof.ProcessEpoch < 1
            || proof.RecoveryVersion < 0 || proof.MembershipVersion < 1
            || !EnrollmentProofs.IsChallenge(proof.BootChallenge) || !EnrollmentProofs.IsChallenge(proof.Nonce))
        {
            throw new InvalidDataException("VoterCredentialContinuationProofScopeInvalid");
        }

        return StrictOnboardingJson.Encode(new
        {
            Domain = "UptimeHarbor.VoterCredential.ContinuationPoP.v1", RequestDigest = requestDigest,
            Proof = proof with { Signature = string.Empty }
        });
    }
}
