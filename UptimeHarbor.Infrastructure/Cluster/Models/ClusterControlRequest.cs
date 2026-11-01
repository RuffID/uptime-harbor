using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster.Models;

/// <summary>Подписанный локальный запрос, связанный с конкретным запуском и карантином.</summary>
public record ClusterControlRequest(Guid RequestId, Guid Challenge, Guid ClusterId, long Generation,
    Guid NodeId, Guid StorageGeneration, string Action, DateTimeOffset IssuedAtUtc, DateTimeOffset ExpiresAtUtc)
{
    public RecoveryAdmission? Admission { get; init; }
    public string? ProtectedBootstrapCommand { get; init; }
    public long RecoveryVersion { get; init; }
    public long OperationsRevision { get; init; }
    public Guid MaintenanceId { get; init; }
    public string IsolationEvidenceDigest { get; init; } = string.Empty;
    public ManualIsolationConfirmation? Isolation { get; init; }

    public override string ToString()
    {
        return $"ClusterControlRequest {RequestId}";
    }
}
