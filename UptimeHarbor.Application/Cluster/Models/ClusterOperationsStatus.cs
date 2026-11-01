namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Публичный реестр для Administrator без сертификатов, payload и локальных секретов.</summary>
public record ClusterOperationsStatus(long Revision, long MembershipVersion, Guid MaintenanceId, long BotId,
    IReadOnlyList<OperationalNode> Nodes, IReadOnlyList<ClusterTrustStatus> Trust, IReadOnlyList<MembershipOperation> Changes);

public record ClusterTrustStatus(Guid Id, Guid NodeId, string Fingerprint, DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? AdmissionExpiresAtUtc, DateTimeOffset? ConsumedAtUtc, DateTimeOffset? RevokedAtUtc);
