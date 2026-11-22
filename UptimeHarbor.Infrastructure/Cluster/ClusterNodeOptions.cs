namespace UptimeHarbor.Infrastructure.Cluster;

public class ClusterNodeOptions
{
    public int Number { get; set; }
    public Guid NodeId { get; set; }
    public string Address { get; set; } = string.Empty;
}
