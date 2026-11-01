using DotNext.Net.Cluster.Consensus.Raft.Http;

namespace UptimeHarbor.Web.Cluster;

/// <summary>Текущий экземпляр принадлежит runtime; writer не зависит от его sender lifecycle.</summary>
public class ClusterRaftHandle
{
    private volatile IRaftHttpCluster? current;

    public IRaftHttpCluster? Current
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
