using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;

/// <summary>Повторно проверяет typed gateway intent перед узкой capability принимающего leader.</summary>
public class EnrollmentCommandReceiver(EnrollmentAuthority authority, RevisionDispatcher dispatcher,
    RevisionStateStore revisions, IClusterStateView state, IEnrollmentReadBarrier barrier,
    IOptions<ClusterOptions> options, IOptions<InstallationOptions> installation, TimeProvider time)
{
    public async Task<CommandResult> AcceptAsync(EnrollmentCoordinatedRequest call, Guid source, Guid storage,
        CancellationToken token)
    {
        await barrier.ConfirmAsync(token);
        RevisionImage image = revisions.Current ?? throw new InvalidOperationException("EnrollmentProjectionUnavailable");
        if (state.Consensus?.IsLeader != true || state.Consensus.NodeId != options.Value.NodeId)
        {
            throw new InvalidOperationException("EnrollmentCoordinatorChanged");
        }

        EnrollmentPeerAuthority.RequireVoter(image, source, storage);
        RevisionCommand command = call.Command;
        RevisionCodec.Validate(command);
        EnrollmentRequest request = call.Request;
        EnrollmentProof proof = call.Proof;
        OnboardingCommand data = command.Onboarding ?? throw new UnauthorizedAccessException("EnrollmentCoordinatorScopeInvalid");
        bool supported = command.Action is "ReserveInvitation" or "AuthorizeEnrollmentCertificate"
            or "BindEnrollmentCredential" or "AdmitInvitedNode" or "ResumeOnboardingAttempt"
            or "RenewOnboardingAdmission" or "RenewOnboardingProcess";
        bool exact = call.Version == 1 && supported && command.Schema == 3 && command.OriginNodeId == source
            && data.StorageGeneration == storage && command.ClusterId == image.ClusterId
            && command.ClusterGeneration == image.Generation && command.ClusterId == request.ClusterId
            && command.ClusterGeneration == request.Generation && command.OperationId == proof.RequestId
            && command.Action == proof.Action && data.InvitationId == request.InvitationId && data.AttemptId == request.AttemptId
            && data.BootId == proof.BootId && data.BootChallenge == proof.BootChallenge && data.ProcessEpoch == proof.ProcessEpoch
            && data.CoordinatorAtUtc is null && data.CoordinatorTerm == 0 && call.CsrDer.Length is > 0 and <= 87384;
        if (!exact)
        {
            throw new UnauthorizedAccessException("EnrollmentCoordinatorBindingInvalid");
        }

        new SetupPeerAddressPolicy(installation.Value).ValidateUri(request.PeerUri);
        byte[] csr = Convert.FromBase64String(call.CsrDer);
        try
        {
            // Nonce уже одноразово потреблён authenticated ingress voter; повторяется криптографический PoP.
            if (!EnrollmentProofs.Verify(request, proof, csr))
            {
                throw new UnauthorizedAccessException("EnrollmentCoordinatorProofInvalid");
            }

            RequirePayload(image, call);
            return await authority.AcceptAsync(dispatcher, command, token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(csr);
        }
    }

    private void RequirePayload(RevisionImage image, EnrollmentCoordinatedRequest call)
    {
        RevisionCommand command = call.Command;
        OnboardingCommand data = command.Onboarding!;
        EnrollmentRequest request = call.Request;
        if (command.Action == "ReserveInvitation")
        {
            if (data.Request != request || data.RequestDigest != call.Proof.RequestDigest || call.Artifact is not null)
            {
                throw new UnauthorizedAccessException("EnrollmentCoordinatorReservationInvalid");
            }

            return;
        }

        OnboardingAttempt? attempt = image.Onboarding?.Invitations.GetValueOrDefault(request.InvitationId)?.Attempt;
        if (attempt is null || attempt.Request != request || attempt.RequestDigest != call.Proof.RequestDigest)
        {
            throw new UnauthorizedAccessException("EnrollmentCoordinatorAttemptInvalid");
        }

        if (command.Action == "AuthorizeEnrollmentCertificate")
        {
            EnrollmentIssuance issuance = data.Issuance ?? throw new UnauthorizedAccessException("EnrollmentTemplateMissing");
            EnrollmentCertificates.ValidateTemplate(request, issuance);
            EnrollmentPeerAuthority.RequireIssuer(image, issuance.IssuerEpoch, time.GetUtcNow());
            if (issuance.IssueId != command.OperationId || call.Artifact is not null)
            {
                throw new UnauthorizedAccessException("EnrollmentCoordinatorTemplateInvalid");
            }

            return;
        }

        bool leaf = command.Action is "BindEnrollmentCredential" or "AdmitInvitedNode";
        if (!leaf)
        {
            if (call.Artifact is not null)
            {
                throw new UnauthorizedAccessException("EnrollmentCoordinatorArtifactUnexpected");
            }

            return;
        }

        EnrollmentIssuedArtifact artifact = call.Artifact ?? throw new UnauthorizedAccessException("EnrollmentArtifactRequired");
        EnrollmentArtifactValidation.Require(image, request, attempt.Issuance
            ?? throw new UnauthorizedAccessException("EnrollmentIssuanceRequired"), artifact, time.GetUtcNow());
        if (command.Action == "BindEnrollmentCredential")
        {
            if (data.IssueId != artifact.IssueId || data.CertificateDigest != artifact.CertificateDigest)
            {
                throw new UnauthorizedAccessException("EnrollmentCoordinatorBindInvalid");
            }
        }
        else
        {
            OperationalNode node = data.Node!;
            OperationalCredential credential = data.Credential!;
            if (node.NodeId != request.NodeId || node.StorageGeneration != request.StorageGeneration
                || node.Address != request.PeerUri || node.Voting || node.Activated || node.Removed
                || credential.Id != artifact.IssueId || credential.NodeId != request.NodeId
                || credential.Fingerprint != artifact.CertificateDigest
                || credential.PublicCertificate != Convert.ToBase64String(artifact.CertificateDer)
                || credential.AdmissionId != command.OperationId)
            {
                throw new UnauthorizedAccessException("EnrollmentCoordinatorAdmissionInvalid");
            }
        }
    }
}
