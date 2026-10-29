using System.Collections.Immutable;

namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Доказательство внешней процедуры восстановления; не выводится из CanConnect или наличия файла.</summary>
public record RecoveryAdmission(Guid ClusterId, long Generation, Guid NodeId, Guid StorageGeneration,
    bool FencingConfirmed, bool OldBackup, Guid? SnapshotId, string? SnapshotDigest,
    long MinimumCommittedIndex, long MembershipVersion)
{
    // Только внешний проверенный provenance. Пароль в открытом виде здесь недопустим.
    public RevisionCommand? BootstrapAccount { get; init; }
    public ImmutableDictionary<Guid, Guid> MonitoringNodes { get; init; } = ImmutableDictionary<Guid, Guid>.Empty;

    public override string ToString()
    {
        return $"RecoveryAdmission {ClusterId}/{Generation} for {NodeId}";
    }
}
