using DotNext.Net.Cluster.Consensus.Raft;

namespace UptimeHarbor.Web.Cluster;

/// <summary>Текущий экземпляр принадлежит runtime; writer не зависит от его sender lifecycle.</summary>
public class ClusterRaftHandle
{
    private volatile IRaftCluster? current;

    public IRaftCluster? Current
    {
        get
        {
            return current;
        }
        internal set
        {
            current = value;
        }
    }
}
