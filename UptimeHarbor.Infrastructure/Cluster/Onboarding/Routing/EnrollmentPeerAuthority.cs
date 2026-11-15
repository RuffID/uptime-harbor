using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;

/// <summary>Authenticated identity дополнительно сверяется с текущим принятым voter/storage.</summary>
public static class EnrollmentPeerAuthority
{
    public static OperationalNode RequireVoter(RevisionImage image, Guid nodeId, Guid storage)
    {
        OperationalNode? node = image.Operations.Nodes.GetValueOrDefault(nodeId);
        if (node is null || !node.Voting || node.Removed || node.StorageGeneration != storage
            || image.Operations.MaintenanceId != Guid.Empty)
        {
            throw new UnauthorizedAccessException("EnrollmentPeerVoterRequired");
        }

        return node;
    }

    public static AcceptedEnrollmentIssuer RequireIssuer(RevisionImage image, Guid epoch, DateTimeOffset now)
    {
        AcceptedEnrollmentIssuer? issuer = image.OnboardingContracts?.Issuers?.GetValueOrDefault(epoch);
        if (issuer is null || issuer.RetiredAtUtc.HasValue || issuer.RevokedAtUtc.HasValue
            || now < issuer.NotBeforeUtc || now >= issuer.NotAfterUtc)
        {
            throw new UnauthorizedAccessException("EnrollmentPeerIssuerUnavailable");
        }

        RequireVoter(image, issuer.NodeId, issuer.StorageGeneration);
        return issuer;
    }
}
