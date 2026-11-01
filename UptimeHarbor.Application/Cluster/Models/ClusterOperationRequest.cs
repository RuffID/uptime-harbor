namespace UptimeHarbor.Application.Cluster.Models;

public record ClusterOperationRequest(Guid OperationId, string Action, Guid NodeId, long BaseRevision, long MembershipVersion)
{
    public Guid StorageGeneration { get; init; }
    public string? Address { get; init; }
    public Guid CredentialId { get; init; }
    public Guid MembershipChangeId { get; init; }
    public string? PublicCertificate { get; init; }
    public bool Remove { get; init; }
    public Guid MaintenanceId { get; init; }
    public Guid? TargetOperationId { get; init; }
    public Guid? ReplacementOperationId { get; init; }
    public string? ReplacementAction { get; init; }
    public bool ConfirmRestored { get; init; }
}
