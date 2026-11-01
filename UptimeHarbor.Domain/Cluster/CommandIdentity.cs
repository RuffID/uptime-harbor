namespace UptimeHarbor.Domain.Cluster;

public record CommandIdentity
{
    public CommandIdentity(Guid operationId, Guid originNodeId, Guid? actorUserId, string? systemSource)
    {
        StorageGuards.Id(operationId, nameof(operationId));
        StorageGuards.Id(originNodeId, nameof(originNodeId));
        if (actorUserId == Guid.Empty || actorUserId.HasValue == !string.IsNullOrWhiteSpace(systemSource))
        {
            throw new ArgumentException("Exactly one actor or system source is required.");
        }

        OperationId = operationId;
        OriginNodeId = originNodeId;
        ActorUserId = actorUserId;
        SystemSource = systemSource;
    }

    public Guid OperationId { get; }
    public Guid OriginNodeId { get; }
    public Guid? ActorUserId { get; }
    public string? SystemSource { get; }
}
