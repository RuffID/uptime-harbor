namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Внутренний payload журнала, не браузерный DTO и не доказательство сам по себе.</summary>
public record ClusterOperation
{
    public Guid StorageGeneration { get; init; }
    public OperationalNode? Node { get; init; }
    public OperationalCredential? Credential { get; init; }
    public Guid ChangeId { get; init; }
    public long MembershipVersion { get; init; }
    public long CoordinatorTerm { get; init; }
    public bool Remove { get; init; }
    public SenderAuthorityKey? Sender { get; init; }
    public Guid MaintenanceId { get; init; }
}
