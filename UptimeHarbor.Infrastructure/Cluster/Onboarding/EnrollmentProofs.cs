using System.Security.Cryptography;
using System.Text.Json;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Подпись фиксированных полей PoP; общий Sign(bytes) наружу не предоставляется.</summary>
public static class EnrollmentProofs
{
    public static byte[] Canonical(EnrollmentRequest request, EnrollmentProof proof)
    {
        StorageGuards.Id(proof.RequestId, nameof(proof.RequestId));
        StorageGuards.Id(proof.BootId, nameof(proof.BootId));
        bool action = proof.Action is "ReserveInvitation" or "AuthorizeEnrollmentCertificate" or "BindEnrollmentCredential"
            or "AdmitInvitedNode" or "ResumeOnboardingAttempt" or "RenewOnboardingAdmission" or "RenewOnboardingProcess"
            or "AuthorizeOnboardingActivation" or "EnableOnline" or "CompleteOnboardingAttempt" or "RenewCredential"
            or "ReadEnrollmentAttempt" or "ProbeEnrollmentPeers";
        if (proof.Version != 1 || proof.ProcessEpoch < 1 || !action
            || proof.RequestDigest != Application.Cluster.Onboarding.EnrollmentRequestCodec.Digest(request)
            || !IsChallenge(proof.BootChallenge) || !IsChallenge(proof.Nonce))
        {
            throw new InvalidDataException("EnrollmentProofInvalid");
        }

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("action", proof.Action);
            writer.WriteString("attemptId", request.AttemptId.ToString("D"));
            writer.WriteString("bootChallenge", proof.BootChallenge);
            writer.WriteString("bootId", proof.BootId.ToString("D"));
            writer.WriteString("clusterId", request.ClusterId.ToString("D"));
            writer.WriteString("domain", "UptimeHarbor.Enrollment.PoP.v1");
            writer.WriteNumber("generation", request.Generation);
            writer.WriteString("invitationId", request.InvitationId.ToString("D"));
            writer.WriteString("nodeId", request.NodeId.ToString("D"));
            writer.WriteString("nonce", proof.Nonce);
            writer.WriteNumber("processEpoch", proof.ProcessEpoch);
            writer.WriteString("requestDigest", proof.RequestDigest);
            writer.WriteString("requestId", proof.RequestId.ToString("D"));
            writer.WriteString("storageGeneration", request.StorageGeneration.ToString("D"));
            writer.WriteNumber("version", proof.Version);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static EnrollmentProof Sign(EnrollmentRequest request, EnrollmentProof proof, ECDsa key)
    {
        if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value
            || EnrollmentCertificates.Hash(key.ExportSubjectPublicKeyInfo()) != request.SpkiDigest)
        {
            throw new InvalidDataException("EnrollmentProofKeyMismatch");
        }

        byte[] signature = key.SignData(Canonical(request, proof), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return proof with { Signature = Convert.ToBase64String(signature) };
    }

    public static bool Verify(EnrollmentRequest request, EnrollmentProof proof, ReadOnlySpan<byte> csr)
    {
        try
        {
            CertificateRequestPublicKey(request, csr, out byte[] publicKey);
            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
            byte[] signature = Convert.FromBase64String(proof.Signature);
            if (signature.Length is < 8 or > 80)
            {
                return false;
            }

            return key.VerifyData(Canonical(request, proof), signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception error) when (error is CryptographicException or InvalidDataException or ArgumentException or FormatException)
        {
            return false;
        }
    }

    private static void CertificateRequestPublicKey(EnrollmentRequest request, ReadOnlySpan<byte> csr, out byte[] publicKey)
    {
        publicKey = EnrollmentCertificates.ReadCsr(csr, request).PublicKey.ExportSubjectPublicKeyInfo();
    }

    public static bool IsChallenge(string value)
    {
        if (value.Length != 44)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[32];
        return Convert.TryFromBase64String(value, bytes, out int written) && written == 32
            && Convert.ToBase64String(bytes) == value;
    }
}
