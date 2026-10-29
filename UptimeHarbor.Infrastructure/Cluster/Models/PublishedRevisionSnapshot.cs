using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster.Models;

public record PublishedRevisionSnapshot(RevisionSnapshotManifest Manifest, string Digest, string Path, RevisionImage Image)
{
    public ClusterSnapshot AsRaftSnapshot()
    {
        return new ClusterSnapshot(Manifest.Index, Manifest.Term, new FileInfo(Path).Length,
            () => new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));
    }

    public override string ToString()
    {
        return $"Snapshot {Manifest.SnapshotId} at {Manifest.Index}";
    }
}
