using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster;

public class ClusterRuntimeState(IClusterRecovery recovery) : IClusterStateView
{
    private ConsensusStatus? status;
    public ConsensusStatus? Consensus => Volatile.Read(ref status);
    public AcceptedClusterState Accepted => recovery.Accepted;

    public void Publish(ConsensusStatus? value)
    {
        Volatile.Write(ref status, value);
    }
}
