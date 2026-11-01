namespace UptimeHarbor.Domain.Cluster;

public class MembershipChange : ProjectionRecord
{
    private MembershipChange()
    {
    }

    public MembershipChange(Guid id, JournalPosition position, Guid nodeId, long expectedVersion,
        MembershipChangeState state) : base(id, position)
    {
        StorageGuards.Id(nodeId, nameof(nodeId));
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        NodeId = nodeId;
        ExpectedVersion = expectedVersion;
        State = state;
    }

    public Guid NodeId { get; private set; }
    public long ExpectedVersion { get; private set; }
    public MembershipChangeState State { get; private set; }
}
