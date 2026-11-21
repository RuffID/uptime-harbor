using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;

namespace UptimeHarbor.Web.Cluster.EnrollmentRouting;

internal record VoterCredentialPeerEvidence(VoterCredentialRotation Authorization, CommandResult Result, AcceptedEnrollmentIssuer Issuer,
    OperationalCredential SubjectCredential, OperationalCredential IssuerCredential)
{
    public static VoterCredentialPeerEvidence Require(RevisionImage image, VoterCredentialSigningRequest call,
        Guid issuerNodeId, Guid issuerStorage, Guid issuerCredentialId, DateTimeOffset now, VoterCredentialLegacyTrust? legacy = null)
    {
        VoterCredentialRenewal renewal = call.ProofRequest.Request.Renewal;
        VoterCredentialRotation authorization = VoterCredentialAuthorityPolicy.RequireAccepted(image, call, now, legacy);
        CommandResult result = image.Results[authorization.AuthorizationOperationId];
        AcceptedEnrollmentIssuer issuer = EnrollmentPeerAuthority.RequireIssuer(image, renewal.Issuance.IssuerEpoch, now);
        OperationalCredential subjectCredential = image.Operations.Credentials[renewal.PreviousCredentialId];
        OperationalCredential? issuerCredential = image.Operations.Credentials.GetValueOrDefault(issuerCredentialId);
        OperationalNode issuerNode = image.Operations.Nodes[issuer.NodeId];
        if (issuer.NodeId != issuerNodeId || issuer.StorageGeneration != issuerStorage
            || issuerCredential is null || issuerCredential.NodeId != issuerNodeId
            || issuerCredential.AdmissionId.HasValue && !issuerCredential.ConsumedAtUtc.HasValue
            || !EnrollmentPeerTrustPolicy.IsActive(image, issuerNode, issuerCredential, now))
        {
            throw new UnauthorizedAccessException("VoterCredentialPeerIssuerMismatch");
        }

        return new(authorization, result, issuer, subjectCredential, issuerCredential);
    }
}
