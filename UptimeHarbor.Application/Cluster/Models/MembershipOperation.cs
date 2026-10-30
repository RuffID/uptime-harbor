namespace UptimeHarbor.Application.Cluster.Models;

public record MembershipOperation(Guid Id, Guid ActorUserId, long ActorRevision, Guid NodeId, bool Remove,
    long ExpectedVersion, long? ConfirmedVersion, long Revision)
{
    public DateTimeOffset RequestedAtUtc { get; init; }
}
