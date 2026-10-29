using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Web.Cluster;

public class ClusterConsensusWriter(ClusterPeerRuntime runtime, ClusterRuntimeState state,
    NodeReadiness readiness, IOptions<ClusterOptions> options, RevisionStateStore? accepted = null) : IConsensusWriter
{
    public ValueTask<long> ReplicateAsync(ReadOnlyMemory<byte> payload, int commandId, CancellationToken cancellationToken)
    {
        if (runtime.Raft is null)
        {
            throw new InvalidOperationException("Raft recovery is unconfirmed.");
        }

        DotNextConsensusAdapter adapter = new(runtime.Raft, state, readiness, options.Value, accepted);
        return adapter.ReplicateAsync(payload, commandId, cancellationToken);
    }

    public ValueTask<bool> ResignAsync(CancellationToken cancellationToken)
    {
        if (runtime.Raft is null)
        {
            return ValueTask.FromResult(false);
        }

        return runtime.Raft.ResignAsync(cancellationToken);
    }
}
