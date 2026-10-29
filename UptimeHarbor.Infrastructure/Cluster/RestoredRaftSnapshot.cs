using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster;

internal class RestoredRaftSnapshot(ClusterSnapshot snapshot) : ISnapshot
{
    public long Index => snapshot.Index;
    public long Term => snapshot.Term;
    public long? Length => snapshot.Length;
    public int? CommandId => null;
    public bool IsReusable => true;

    public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : IAsyncBinaryWriter
    {
        await using Stream stream = snapshot.OpenRead();
        await writer.CopyFromAsync(stream, snapshot.Length, token);
    }
}
