using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster.Onboarding;

/// <summary>Активация впервые открывает рабочую роль voter; catch-up и Enable её не заменяют.</summary>
public static class OnlineActivationValidation
{
    public static bool Current(RevisionImage image, OnlineGrant grant, DateTimeOffset now)
    {
        OperationalNode? node = image.Operations.Nodes.GetValueOrDefault(grant.NodeId);
        return grant.Purpose == OnlineGrantPurpose.Activation && node is not null && !node.Activated
            && OnlineGrantPolicy.Current(image, grant, now);
    }
}
