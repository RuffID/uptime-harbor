namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Создаётся SignedRecoveryAdmission только после проверки operator signature и ручной изоляции.</summary>
public sealed record SignedOfflineRecoveryProof(string IsolationEvidenceDigest) : RecoveryProof;
