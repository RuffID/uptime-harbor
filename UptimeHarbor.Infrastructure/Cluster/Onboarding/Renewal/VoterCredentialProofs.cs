using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

/// <summary>Раздельные immutable intent и короткий operational-key proof текущего процесса.</summary>
public static class VoterCredentialProofs
{
    public const string SCOPE = "RenewVoterCredential";

    public static string Digest(VoterCredentialRequest request)
    {
        return VoterContinuationCommands.IntentDigest(request);
    }

    public static byte[] Canonical(VoterCredentialRequest request, VoterCredentialProof proof)
    {
        string digest = Digest(request);
        StorageGuards.Utc(proof.IssuedAtUtc);
        StorageGuards.Id(proof.BootId, nameof(proof.BootId));
        EnrollmentIssuance leaf = request.Renewal.Issuance;
        if (proof.Version != 1 || proof.Scope != SCOPE || proof.OriginNodeId != leaf.NodeId
            || proof.OriginStorageGeneration != leaf.StorageGeneration || proof.ProcessEpoch < 1
            || proof.RecoveryVersion < 0 || proof.MembershipVersion < 1
            || !EnrollmentProofs.IsChallenge(proof.BootChallenge) || !EnrollmentProofs.IsChallenge(proof.Nonce))
        {
            throw new InvalidDataException("VoterCredentialProofScopeInvalid");
        }

        return StrictOnboardingJson.Encode(new
        {
            Domain = "UptimeHarbor.VoterCredential.PoP.v1", RequestDigest = digest,
            Proof = proof with { Signature = string.Empty }
        });
    }

    public static VoterCredentialProof Sign(VoterCredentialRequest request, VoterCredentialProof proof, ECDsa previousKey)
    {
        return Sign(request, proof, previousKey, Canonical(request, proof));
    }

    public static VoterCredentialProof SignBind(VoterCredentialSigningRequest signing, RevisionCommand command,
        VoterCredentialProof proof, ECDsa previousKey)
    {
        return Sign(signing.ProofRequest.Request, proof, previousKey, CanonicalBind(signing, command, proof));
    }

    internal static VoterCredentialProof Sign(VoterCredentialRequest request, VoterCredentialProof proof, ECDsa previousKey,
        byte[] canonical)
    {
        if (EnrollmentCertificates.Hash(previousKey.ExportSubjectPublicKeyInfo()) != request.Renewal.PreviousSpkiDigest
            || previousKey.KeySize != 256 || previousKey.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
        {
            throw new InvalidDataException("VoterCredentialPreviousKeyInvalid");
        }

        byte[] signature = previousKey.SignData(canonical, HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        return proof with { Signature = Convert.ToBase64String(signature) };
    }

    public static void Require(RevisionImage image, VoterCredentialProofRequest call, DateTimeOffset now, VoterCredentialLegacyTrust? legacy = null)
    {
        Require(image, call, now, legacy, false);
    }

    internal static void Require(RevisionImage image, VoterCredentialProofRequest call, DateTimeOffset now,
        VoterCredentialLegacyTrust? legacy, bool exactBoundReplay)
    {
        VoterCredentialRequest request = call.Request;
        VoterCredentialProof proof = call.Proof;
        Require(image, call, now, legacy, exactBoundReplay, Canonical(request, proof));
    }

    internal static void RequireBind(RevisionImage image, VoterCredentialBindCall call, DateTimeOffset now,
        VoterCredentialLegacyTrust legacy, bool exactBoundReplay)
    {
        VoterCredentialProofRequest request = call.Signing.ProofRequest with { Proof = call.BindProof };
        Require(image, request, now, legacy, exactBoundReplay, CanonicalBind(call.Signing, call.Command, call.BindProof));
    }

    private static byte[] CanonicalBind(VoterCredentialSigningRequest signing, RevisionCommand command, VoterCredentialProof proof)
    {
        return StrictOnboardingJson.Encode(new
        {
            Domain = "UptimeHarbor.VoterCredential.BindPoP.v1",
            signing.AuthorizationIndex,
            CommandDigest = RevisionCodec.Digest(command),
            ProcessProof = Canonical(signing.ProofRequest.Request, proof)
        });
    }

    private static void Require(RevisionImage image, VoterCredentialProofRequest call, DateTimeOffset now,
        VoterCredentialLegacyTrust? legacy, bool exactBoundReplay, byte[] canonical)
    {
        VoterCredentialRequest request = call.Request;
        if (request.BoundSource is not null)
        {
            throw new UnauthorizedAccessException("VoterContinuationBoundRecoveryCannotIssue");
        }

        VoterCredentialRenewal renewal = request.Renewal;
        VoterCredentialProof proof = call.Proof;
        VoterCredentialAuthorityPolicy.RequireCurrent(image, request, now, exactBoundReplay);
        RequireProcess(image, request, proof, now);

        byte[] csr = ReadCsr(call);
        EnrollmentCertificates.ReadCsr(csr, CertificateIdentity(request));
        OperationalCredential old = image.Operations.Credentials[renewal.PreviousCredentialId];
        using X509Certificate2 previous = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(old.PublicCertificate));
        using ECDsa? key = previous.GetECDsaPublicKey();
        if (key is null || key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value
            || EnrollmentCertificates.Hash(previous.RawData) != renewal.PreviousFingerprint
            || EnrollmentCertificates.Hash(previous.PublicKey.ExportSubjectPublicKeyInfo()) != renewal.PreviousSpkiDigest
            || new DateTimeOffset(previous.NotAfter.ToUniversalTime()) != old.ExpiresAtUtc
            || !VoterCredentialLeafTrust.ValidatePrevious(image, old, previous, now, legacy) || proof.Signature.Length is < 12 or > 112)
        {
            throw new UnauthorizedAccessException("VoterCredentialPreviousLeafInvalid");
        }

        byte[] signature = Convert.FromBase64String(proof.Signature);
        if (Convert.ToBase64String(signature) != proof.Signature
            || !key.VerifyData(canonical, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
        {
            throw new UnauthorizedAccessException("VoterCredentialProofInvalid");
        }
    }

    internal static void RequireProcess(RevisionImage image, VoterCredentialRequest request, VoterCredentialProof proof,
        DateTimeOffset now)
    {
        OnlineProcessLease? process = image.OnboardingContracts?.OnlineProcesses?.GetValueOrDefault(request.Renewal.Issuance.NodeId);
        if (process is not { CredentialContinuation: false } || process.BootId != proof.BootId || process.BootChallenge != proof.BootChallenge
            || process.ProcessEpoch != proof.ProcessEpoch || process.RecoveryVersion != proof.RecoveryVersion
            || now >= process.LeaseUntilUtc || proof.MembershipVersion != image.Operations.MembershipVersion
            || proof.IssuedAtUtc > now || now - proof.IssuedAtUtc >= TimeSpan.FromSeconds(60))
        {
            throw new UnauthorizedAccessException("VoterCredentialProcessNotCurrent");
        }
    }

    public static byte[] ReadCsr(VoterCredentialProofRequest call)
    {
        if (call.CsrDer.Length is < 1 or > 87384)
        {
            throw new InvalidDataException("VoterCredentialCsrLimit");
        }

        byte[] csr = Convert.FromBase64String(call.CsrDer);
        if (Convert.ToBase64String(csr) != call.CsrDer || csr.Length > EnrollmentCertificates.MAX_DER_BYTES)
        {
            throw new InvalidDataException("VoterCredentialCsrInvalid");
        }

        return csr;
    }

    // Только адаптация фиксированной X.509 policy; эти correlation IDs не создают attempt/invitation.
    public static EnrollmentRequest CertificateIdentity(VoterCredentialRequest request)
    {
        EnrollmentIssuance leaf = request.Renewal.Issuance;
        return new(1, request.ClusterId, request.Generation, request.OperationId, request.OperationId,
            leaf.NodeId, leaf.StorageGeneration, leaf.PeerUri, leaf.SpkiDigest, leaf.CsrDigest);
    }

}
