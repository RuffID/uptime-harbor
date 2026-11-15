using System.Reflection;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Consensus.Raft;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

/// <summary>Только in-memory native API без Start/elections. Leader/term меняются тестом.</summary>
public class EnrollmentRoutingNative : ManagedRaftProbe
{
    public ClusterMemberId LeaderId { get; set; }
    public bool Remote { get; set; }
    public Action? OnBarrier { get; set; }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == "get_Leader")
        {
            object member = Create(method.ReturnType, typeof(Member));
            Member proxy = (Member)member;
            proxy.Id = LeaderId;
            proxy.Remote = Remote;
            return member;
        }

        if (method?.Name == "ApplyReadBarrierAsync")
        {
            OnBarrier?.Invoke();
        }

        return base.Invoke(method, args);
    }

    public class Member : DispatchProxy
    {
        public ClusterMemberId Id { get; set; }
        public bool Remote { get; set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "get_Id")
            {
                return Id;
            }

            if (method?.Name == "get_IsRemote")
            {
                return Remote;
            }

            throw new NotSupportedException(method?.Name);
        }
    }

    public class Recovery(RevisionStateStore revisions) : IClusterRecovery
    {
        public long? AppliedOverride { get; set; }
        public AcceptedClusterState Accepted => new(true, true, true, true, true,
            AppliedOverride ?? revisions.Current!.Index, 17, Guid.Empty, false);
        public ClusterSnapshot? Snapshot => null;

        public ValueTask<bool> RestoreAsync(Guid cluster, long generation, Guid node, Guid storage, CancellationToken token)
        {
            throw new InvalidOperationException("TestNativeStartForbidden");
        }

        public ValueTask<bool> ConfirmLocalReplayAsync(long committed, long applied, long membership, CancellationToken token)
        {
            throw new NotSupportedException();
        }

        public ValueTask<long> ApplyAsync(CommittedRecord entry, CancellationToken token)
        {
            throw new NotSupportedException();
        }

        public ValueTask ReclaimAsync(long watermark, CancellationToken token)
        {
            throw new NotSupportedException();
        }
    }
}
