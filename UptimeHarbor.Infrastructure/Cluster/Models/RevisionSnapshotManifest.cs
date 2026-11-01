namespace UptimeHarbor.Infrastructure.Cluster.Models;

public record RevisionSnapshotManifest(Guid SnapshotId, Guid ClusterId, long Generation,
    long Index, long Term, string Compatibility, long BodyBytes, string BodyDigest, int ChunkBytes,
    IReadOnlyList<string> ChunkDigests);
