using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster;

public interface IRevisionProjectionStore
{
    Task ApplyAsync(ProjectionWriteSet changes, CancellationToken cancellationToken);
    Task InstallAsync(RevisionImage image, Guid snapshotId, string snapshotDigest, CancellationToken cancellationToken);
    Task VerifyAsync(RevisionImage image, CancellationToken cancellationToken);
    Task<RevisionImage> ExportAsync(RevisionImage image, CancellationToken cancellationToken);
    Task CheckpointAsync(Guid snapshotId, string digest, long index, CancellationToken cancellationToken);
    Task<ProjectionHead?> ReadHeadAsync(CancellationToken cancellationToken);
}
