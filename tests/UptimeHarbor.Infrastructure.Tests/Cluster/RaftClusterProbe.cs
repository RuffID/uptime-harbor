using System.Reflection;
using DotNext.Net.Cluster.Consensus.Raft;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class RaftClusterProbe : DispatchProxy
{
    public IPersistentState Store { get; set; } = null!;
    public Func<IRaftLogEntry, CancellationToken, ValueTask> Replicate { get; set; } = null!;
    public int Calls { get; private set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case "get_AuditTrail":
                return Store;
            case "get_LeadershipToken":
            case "get_ConsensusToken":
                return CancellationToken.None;
            case "ReplicateAsync":
                Calls++;
                return Replicate((IRaftLogEntry)args![0]!, (CancellationToken)args[1]!);
            default:
                throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
