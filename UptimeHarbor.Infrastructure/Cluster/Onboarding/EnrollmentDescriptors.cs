using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Публичный whitelist descriptor; account state, verifier и private references не передаются.</summary>
public class EnrollmentDescriptors(RevisionStateStore revisions, IOptions<InstallationOptions> installation,
    IOptions<ClusterOptions> options, TimeProvider time)
{
    public EnrollmentDiscovery Discover(Guid? invitationId = null)
    {
        RevisionImage image = RequireImage();
        Guid invitation = invitationId ?? image.Onboarding?.Invitations.Values
            .SingleOrDefault(item => item.Status is InvitationStatus.Unused or InvitationStatus.Reserved)?.Id ?? Guid.Empty;
        string origin = installation.Value.BrowserOrigin + "/";
        EnrollmentEndpoints endpoints = new(new SetupPeerAddressPolicy(installation.Value));
        Uri browser = endpoints.Origin(origin);
        string enrollment = new Uri(browser, EnrollmentEndpoints.PREFIX).AbsoluteUri;
        endpoints.ValidateAdvertised(browser, enrollment, options.Value.PublicUri);
        return new(1, OnboardingProtocol.TRUST_COMPATIBILITY,
            ["csr-p256", "pop-nonce-v1", "accepted-reserve-bind-admit", "process-resume-v1"],
            image.ClusterId, image.Generation, invitation, origin, enrollment, options.Value.PublicUri,
            EnrollmentTransport.MAX_BYTES, 10);
    }

    public EnrollmentDescriptor Bound(EnrollmentRequest request, EnrollmentProof proof, bool readOnly = false)
    {
        RevisionImage image = RequireImage();
        OnboardingAttempt attempt = image.Onboarding?.Invitations.GetValueOrDefault(request.InvitationId)?.Attempt
            ?? throw new UnauthorizedAccessException("EnrollmentReservationMissing");
        RequireCurrent(image, attempt, request);
        if (!readOnly && (attempt.BootId != proof.BootId || attempt.BootChallenge != proof.BootChallenge
            || attempt.ProcessEpoch != proof.ProcessEpoch))
        {
            throw new UnauthorizedAccessException("EnrollmentProcessChanged");
        }

        CommandResult reserved = image.Results[attempt.ReserveOperationId];
        OperationalCredential[] credentials = image.Operations.Credentials.Values
            .Where(item => !item.RevokedAtUtc.HasValue && time.GetUtcNow() < item.ExpiresAtUtc).ToArray();
        string? certificate = credentials.SingleOrDefault(item => item.Fingerprint == attempt.CertificateDigest)?.PublicCertificate;
        return new(1, Discover(request.InvitationId), request, attempt.RequestDigest, attempt.BootId,
            attempt.BootChallenge!, attempt.ProcessEpoch, proof.Nonce, reserved.Index, image.Index,
            image.Operations.BotId, attempt.ReservationUntilUtc, attempt.ProcessLeaseUntilUtc,
            attempt.AdmissionUntilUtc, attempt.Status.ToString(), attempt.Issuance, certificate,
            image.OnboardingContracts!.Issuers!.Values.ToArray(), image.Operations.Nodes.Values.ToArray(), credentials)
        {
            ResponseProof = proof
        };
    }

    public RevisionImage RequireImage()
    {
        RevisionImage? image = revisions.Current;
        if (!revisions.Available || image?.OnboardingContracts?.Issuers is null
            || options.Value.Compatibility != OnboardingProtocol.TRUST_COMPATIBILITY
            || image.Operations.MaintenanceId != Guid.Empty)
        {
            throw new InvalidOperationException("EnrollmentRegistryUnavailable");
        }

        return image;
    }

    public void RequireCurrent(RevisionImage image, OnboardingAttempt attempt, EnrollmentRequest request)
    {
        DateTimeOffset now = time.GetUtcNow();
        OnboardingInvitation invitation = image.Onboarding!.Invitations[request.InvitationId];
        bool active = invitation.Status is InvitationStatus.Reserved or InvitationStatus.Consumed
            && !InvitationTransitions.IsTerminal(attempt.Status)
            && attempt.Status != OnboardingAttemptStatus.PauseAwaitingAdministrator;
        if (!active || attempt.Request != request || attempt.RequestDigest != EnrollmentRequestCodec.Digest(request)
            || !OnboardingReducer.Active(image, attempt.Delegation.ActorId, attempt.Delegation.ActorRevision)
            || now - attempt.LastProgressAtUtc >= TimeSpan.FromHours(24)
            || !attempt.AdmitOperationId.HasValue && now >= attempt.ReservationUntilUtc)
        {
            throw new UnauthorizedAccessException("EnrollmentAttemptNotCurrent");
        }

        if (attempt.Issuance is not null)
        {
            AcceptedEnrollmentIssuer? issuer = image.OnboardingContracts!.Issuers!.GetValueOrDefault(attempt.Issuance.IssuerEpoch);
            if (issuer is null || issuer.RevokedAtUtc.HasValue || now >= issuer.NotAfterUtc)
            {
                throw new UnauthorizedAccessException("EnrollmentIssuerRevoked");
            }
        }

        if (attempt.AdmitOperationId.HasValue)
        {
            OperationalNode? node = image.Operations.Nodes.GetValueOrDefault(request.NodeId);
            OperationalCredential? credential = image.Operations.Credentials.GetValueOrDefault(attempt.IssueId!.Value);
            if (node is null || node.Removed || node.Address != request.PeerUri || node.StorageGeneration != request.StorageGeneration
                || credential is null || credential.RevokedAtUtc.HasValue || now >= credential.ExpiresAtUtc
                || credential.Fingerprint != attempt.CertificateDigest)
            {
                throw new UnauthorizedAccessException("EnrollmentCredentialRevoked");
            }
        }
    }
}
