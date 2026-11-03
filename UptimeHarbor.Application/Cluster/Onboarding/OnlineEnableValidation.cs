using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster.Onboarding;

/// <summary>Enable относится к текущему процессу уже активированного voter и не выполняет recovery/maintenance.</summary>
public static class OnlineEnableValidation
{
    public static bool Current(RevisionImage image, OnlineGrant grant, DateTimeOffset now)
    {
        OperationalNode? node = image.Operations.Nodes.GetValueOrDefault(grant.NodeId);
        return grant.Purpose == OnlineGrantPurpose.Enable && node is not null && node.Activated
            && OnlineGrantPolicy.Current(image, grant, now);
    }
}
