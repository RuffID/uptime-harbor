namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Доказательство внешней процедуры восстановления; не выводится из CanConnect или наличия файла.</summary>
public record RecoveryAdmission(Guid ClusterId, long Generation, Guid NodeId, Guid StorageGeneration,
    bool FencingConfirmed, bool OldBackup, Guid? SnapshotId, string? SnapshotDigest,
    long MinimumCommittedIndex, long MembershipVersion);
