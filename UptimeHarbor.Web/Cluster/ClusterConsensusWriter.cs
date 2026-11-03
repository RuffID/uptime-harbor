using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

namespace UptimeHarbor.Web.Cluster;

public class ClusterConsensusWriter(ClusterRaftHandle handle, ClusterRuntimeState state,
    NodeReadiness readiness, IOptions<ClusterOptions> options, RevisionStateStore? accepted = null,
    GenesisWriterPermit? genesis = null) : IConsensusWriter
{
    public ValueTask<long> ReplicateAsync(ReadOnlyMemory<byte> payload, int commandId, CancellationToken cancellationToken)
    {
        var raft = handle.Current;
        if (raft is null)
        {
            throw new InvalidOperationException("Raft recovery is unconfirmed.");
        }

        DotNextConsensusAdapter adapter = new(raft, state, readiness, options.Value, accepted, genesis);
        return adapter.ReplicateAsync(payload, commandId, cancellationToken);
    }

    public ValueTask<bool> ResignAsync(CancellationToken cancellationToken)
    {
        var raft = handle.Current;
        if (raft is null)
        {
            return ValueTask.FromResult(false);
        }

        return raft.ResignAsync(cancellationToken);
    }
}
