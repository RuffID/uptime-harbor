using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

public static class VoterCredentialBindCommand
{
    public static RevisionCommand Create(VoterCredentialRequest request, EnrollmentIssuedArtifact artifact, Guid operationId)
    {
        EnrollmentIssuance leaf = request.Renewal.Issuance;
        OperationalCredential credential = new(leaf.IssueId, leaf.NodeId, artifact.CertificateDigest,
            Convert.ToBase64String(artifact.CertificateDer), leaf.NotAfterUtc, null, null);
        return CreateFromCredential(request, credential, operationId);
    }

    internal static RevisionCommand CreateFromCredential(VoterCredentialRequest request, OperationalCredential credential, Guid operationId)
    {
        if (operationId == Guid.Empty || operationId == request.OperationId || operationId == request.Renewal.Issuance.IssueId)
        {
            throw new InvalidDataException("VoterCredentialBindOperationInvalid");
        }

        RevisionCommand authorization = VoterCredentialAuthorizationCommand.Create(request);
        RevisionCommand command = authorization with
        {
            OperationId = operationId,
            Action = "BindVoterCredential",
            EnrollmentTrust = authorization.EnrollmentTrust! with { Rotation = new(request.Renewal, credential, null) }
        };
        RevisionCodec.Validate(command);
        return command;
    }
}
