using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster;

public interface IClusterStateView
{
    ConsensusStatus? Consensus { get; }
    AcceptedClusterState Accepted { get; }
}
