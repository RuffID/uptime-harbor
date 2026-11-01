using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Models;

public record AcceptedControl(Guid Id, Guid TargetId, string TargetDigest, ControlKind Kind,
    Guid? ReplacementId, long Index, long Term);
