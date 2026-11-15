using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Проверяет ответ аутентифицированного origin до сохранения anchors/pins. Не доказывает сетевую связность.</summary>
public static class EnrollmentDescriptorValidation
{
    public static void Discovery(EnrollmentDiscovery discovery, Uri origin, EnrollmentEndpoints endpoints)
    {
        string[] required = ["csr-p256", "pop-nonce-v1", "accepted-reserve-bind-admit", "process-resume-v1"];
        if (discovery.Version != 1 || discovery.Compatibility != OnboardingProtocol.TRUST_COMPATIBILITY
            || discovery.ClusterId == Guid.Empty || discovery.Generation < 1 || discovery.InvitationId == Guid.Empty
            || discovery.BrowserOrigin != origin.AbsoluteUri || discovery.MaxBytes != EnrollmentTransport.MAX_BYTES
            || discovery.TimeoutSeconds != 10 || discovery.Capabilities is null
            || required.Any(capability => !discovery.Capabilities.Contains(capability, StringComparer.Ordinal)))
        {
            throw new InvalidDataException("EnrollmentDiscoveryIncompatible");
        }

        endpoints.ValidateAdvertised(origin, discovery.EnrollmentAddress, discovery.PeerAddress);
    }

    public static EnrollmentDescriptor Bound(EnrollmentDescriptor descriptor, LocalOnboardingAttempt local, EnrollmentProof proof,
        EnrollmentEndpoints endpoints, DateTimeOffset now)
    {
        Uri origin = endpoints.Origin(local.BrowserOrigin ?? throw new InvalidDataException("EnrollmentOriginMissing"));
        Discovery(descriptor.Discovery, origin, endpoints);
        EnrollmentRequest request = local.Request;
        bool readOnly = proof.Action == "ReadEnrollmentAttempt";
        bool currentProcess = descriptor.BootId == proof.BootId && descriptor.BootChallenge == proof.BootChallenge
            && descriptor.ProcessEpoch == proof.ProcessEpoch && descriptor.ProcessLeaseUntilUtc > now;
        bool exact = descriptor.Version == 1 && descriptor.Request == request && descriptor.RequestDigest == local.RequestDigest
            && descriptor.Discovery.ClusterId == request.ClusterId && descriptor.Discovery.Generation == request.Generation
            && descriptor.Discovery.InvitationId == request.InvitationId && descriptor.ReservationIndex > 0
            && descriptor.AppliedIndex >= descriptor.ReservationIndex && descriptor.BotId > 0
            && descriptor.ResponseProof == proof && descriptor.Nonce == proof.Nonce && (readOnly || currentProcess)
            && descriptor.Issuers.Length is > 0 and <= 16
            && descriptor.Nodes.Length <= 1000 && descriptor.Credentials.Length <= 2000;
        if (!exact || local.ReservationIndex.HasValue && local.ReservationIndex != descriptor.ReservationIndex)
        {
            throw new InvalidDataException("EnrollmentDescriptorBindingInvalid");
        }

        foreach (AcceptedEnrollmentIssuer issuer in descriptor.Issuers)
        {
            EnrollmentTrustCommands.ValidateIssuer(issuer);
            using X509Certificate2 root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(issuer.CertificateDer));
            if (!EnrollmentCertificates.ValidateIssuer(root, request.ClusterId, request.Generation, issuer.Epoch, now)
                || issuer.SpkiDigest != EnrollmentCertificates.Hash(root.PublicKey.ExportSubjectPublicKeyInfo()))
            {
                throw new InvalidDataException("EnrollmentDescriptorIssuerInvalid");
            }
        }

        if (local.Descriptor is not null)
        {
            EnrollmentDescriptor previous = local.Descriptor;
            if (previous.Issuance is not null && previous.Issuance != descriptor.Issuance)
            {
                throw new InvalidDataException("EnrollmentDescriptorIssuanceChanged");
            }

            if (previous.CertificateDer is not null && previous.CertificateDer != descriptor.CertificateDer)
            {
                // До Bind read/process ответы могут ещё не иметь DER: сохраняются уже полученные байты.
                bool processReply = proof.Action is "ReadEnrollmentAttempt" or "ResumeOnboardingAttempt" or "RenewOnboardingProcess";
                bool awaitingBind = processReply && previous.Status == "CertificateAuthorized"
                    && descriptor.Status == "CertificateAuthorized" && descriptor.CertificateDer is null;
                if (!awaitingBind)
                {
                    throw new InvalidDataException("EnrollmentDescriptorCertificateChanged");
                }

                descriptor = descriptor with { CertificateDer = previous.CertificateDer };
            }

            bool issuerChanged = previous.Issuers.Length != descriptor.Issuers.Length || previous.Issuers.Any(old =>
                !descriptor.Issuers.Any(current => current.Epoch == old.Epoch && current.CertificateDigest == old.CertificateDigest
                    && current.SpkiDigest == old.SpkiDigest && current.CertificateDer == old.CertificateDer));
            if (issuerChanged || previous.Discovery != descriptor.Discovery
                && !System.Text.Json.JsonElement.DeepEquals(System.Text.Json.JsonSerializer.SerializeToElement(previous.Discovery),
                    System.Text.Json.JsonSerializer.SerializeToElement(descriptor.Discovery)))
            {
                throw new InvalidDataException("EnrollmentDescriptorTrustChanged");
            }
        }

        foreach (OperationalCredential credential in descriptor.Credentials)
        {
            if (descriptor.Nodes.Count(node => node.NodeId == credential.NodeId) != 1)
            {
                throw new InvalidDataException("EnrollmentDescriptorOrphanCredential");
            }
        }

        if (descriptor.AdmissionUntilUtc.HasValue && (descriptor.CertificateDer is null || descriptor.Status != "Admitted"))
        {
            throw new InvalidDataException("EnrollmentDescriptorAdmissionInvalid");
        }

        if (descriptor.CertificateDer is not null)
        {
            EnrollmentIssuance template = descriptor.Issuance ?? throw new InvalidDataException("EnrollmentTemplateMissing");
            AcceptedEnrollmentIssuer issuer = descriptor.Issuers.Single(item => item.Epoch == template.IssuerEpoch);
            using X509Certificate2 root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(issuer.CertificateDer));
            using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(descriptor.CertificateDer));
            if (issuer.RevokedAtUtc.HasValue || !EnrollmentCertificates.ValidateLeaf(leaf, root, request, template,
                EnrollmentCertificates.Hash(leaf.RawData), now, true))
            {
                throw new InvalidDataException("EnrollmentDescriptorLeafInvalid");
            }

            if (descriptor.AdmissionUntilUtc.HasValue)
            {
                OperationalNode node = descriptor.Nodes.Single(item => item.NodeId == request.NodeId);
                OperationalCredential credential = descriptor.Credentials.Single(item => item.Id == template.IssueId);
                if (node.Removed || node.StorageGeneration != request.StorageGeneration || node.Address != request.PeerUri
                    || credential.NodeId != node.NodeId || credential.PublicCertificate != descriptor.CertificateDer
                    || credential.Fingerprint != EnrollmentCertificates.Hash(leaf.RawData) || credential.RevokedAtUtc.HasValue
                    || credential.AdmissionExpiresAtUtc != descriptor.AdmissionUntilUtc || !readOnly && descriptor.AdmissionUntilUtc <= now)
                {
                    throw new InvalidDataException("EnrollmentDescriptorAdmissionInvalid");
                }

                if (node.Voting || node.Activated)
                {
                    throw new InvalidDataException("EnrollmentDescriptorScopeInvalid");
                }
            }
        }

        return descriptor;
    }
}
