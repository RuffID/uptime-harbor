namespace UptimeHarbor.Domain.Cluster;

// Происхождение проекции; журнал согласования и принятые позиции определяются вне SQL.
public abstract class ProjectionRecord
{
    protected ProjectionRecord()
    {
    }

    protected ProjectionRecord(Guid id, JournalPosition position)
    {
        StorageGuards.Id(id, nameof(id));
        ArgumentNullException.ThrowIfNull(position);
        Id = id;
        ClusterId = position.Cluster.ClusterId;
        ClusterGeneration = position.Cluster.Generation;
        LogIndex = position.LogIndex;
        Term = position.Term;
    }

    public Guid Id { get; private set; }
    public Guid ClusterId { get; private set; }
    public long ClusterGeneration { get; private set; }
    public long LogIndex { get; private set; }
    public long Term { get; private set; }

    public JournalPosition GetPosition()
    {
        return new JournalPosition(new ClusterIdentity(ClusterId, ClusterGeneration), LogIndex, Term);
    }
}
