namespace UptimeHarbor.Application.Cluster.Models;

public record ConflictVariant(Guid Id, Guid OperationId, Guid EntityId, long BaseRevision,
    long CurrentRevision, string BasePayload, string AcceptedPayload, string ProposedPayload,
    long Index, long Term, DateTimeOffset CreatedAtUtc,
    Guid? ResolutionOperationId = null, DateTimeOffset? ResolvedAtUtc = null)
{
    public override string ToString()
    {
        return $"ConflictVariant {Id}";
    }
}
