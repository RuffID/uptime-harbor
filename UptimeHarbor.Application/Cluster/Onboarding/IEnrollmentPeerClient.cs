using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster.Onboarding;

/// <summary>Узкие authenticated вызовы voter. Не является transport произвольных commands или bytes.</summary>
public interface IEnrollmentPeerClient
{
    Task<CommandResult> AcceptAsync(EnrollmentCoordinatedRequest request, CancellationToken token);
    Task<EnrollmentIssuedArtifact> IssueAsync(Guid issuerNodeId, EnrollmentSigningRequest request, CancellationToken token);
    Task<OnlineGrant> SignGrantAsync(Guid issuerNodeId, OnlineGrantSigningRequest request, CancellationToken token);
}
