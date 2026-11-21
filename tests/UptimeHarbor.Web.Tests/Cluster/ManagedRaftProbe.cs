using System.Reflection;
using DotNext.Net.Cluster.Consensus.Raft;

namespace UptimeHarbor.Tests.Web.Cluster;

/// <summary>Только вызовы native API; никакого Start, выборов, majority или транспорта.</summary>
public class ManagedRaftProbe : DispatchProxy
{
    public IPersistentState Store { get; set; } = null!;
    public Func<IRaftLogEntry, CancellationToken, ValueTask> Replicate { get; set; } = null!;
    public bool Majority { get; set; } = true;
    public bool RemoteLeader { get; set; }
    public int Barriers { get; private set; }
    public Action? OnReadBarrier { get; set; }
    public bool LoseResponse { get; set; }
    public int? LoseResponseAt { get; set; }
    public int Replications { get; private set; }
    public int DroppedResponses { get; private set; }
    private object? leader;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case "get_AuditTrail":
                return Store;
            case "get_LeadershipToken":
            case "get_ConsensusToken":
                return CancellationToken.None;
            case "get_Leader":
                leader ??= Create(targetMethod.ReturnType, typeof(LocalMember));
                ((LocalMember)leader).Remote = RemoteLeader;
                return leader;
            case "ApplyReadBarrierAsync":
                Barriers++;
                if (!Majority)
                {
                    throw new InvalidOperationException("TestMajorityUnavailable");
                }

                OnReadBarrier?.Invoke();
                if (targetMethod.ReturnType == typeof(ValueTask))
                {
                    return ValueTask.CompletedTask;
                }

                return Task.CompletedTask;
            case "ReplicateAsync":
                return ReplicateWithResponseAsync((IRaftLogEntry)args![0]!, (CancellationToken)args[1]!);
            default:
                throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private async ValueTask ReplicateWithResponseAsync(IRaftLogEntry entry, CancellationToken token)
    {
        await Replicate(entry, token);
        Replications++;
        if (LoseResponse || LoseResponseAt == Replications)
        {
            LoseResponse = false;
            LoseResponseAt = null;
            DroppedResponses++;
            throw new HttpRequestException("TestResponseLostAfterCommit");
        }
    }

    public class LocalMember : DispatchProxy
    {
        public bool Remote { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_Id")
            {
                return default(DotNext.Net.Cluster.ClusterMemberId);
            }

            if (targetMethod?.Name == "get_IsRemote")
            {
                return Remote;
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
