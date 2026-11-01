namespace UptimeHarbor.Application.Cluster;

public interface IConsensusWriter
{
    ValueTask<long> ReplicateAsync(ReadOnlyMemory<byte> payload, int commandId, CancellationToken cancellationToken);
    ValueTask<bool> ResignAsync(CancellationToken cancellationToken);
}
