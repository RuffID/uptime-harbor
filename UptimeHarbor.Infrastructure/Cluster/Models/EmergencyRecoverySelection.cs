namespace UptimeHarbor.Infrastructure.Cluster.Models;

/// <summary>Выбор копии и явное согласие с неизвестным диапазоном потерь.</summary>
public record EmergencyRecoverySelection(Guid RecoveryId, Guid AdministratorId, Guid PreviousClusterId,
    long PreviousGeneration, string SnapshotPath, string SnapshotDigest, long SourceIndex, long SourceTerm,
    long? KnownCommittedIndex, string LossAcknowledgement, ManualIsolationConfirmation Isolation);
