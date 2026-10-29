using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Persistence.UnitOfWork;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Cluster;

public class EfRevisionProjectionStore(IServiceScopeFactory scopes, IOptions<ClusterOptions> options) : IRevisionProjectionStore
{
    public async Task<ProjectionHead?> ReadHeadAsync(CancellationToken token)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        ProjectionCursor? cursor = await scope.ServiceProvider.GetRequiredService<IProjectionRepository>()
            .GetCursorAsync(options.Value.NodeId, token);
        if (cursor is null)
        {
            return null;
        }

        return new ProjectionHead(cursor.ClusterId, cursor.ClusterGeneration, cursor.LogIndex, cursor.Term, cursor.Digest);
    }

    public async Task ApplyAsync(ProjectionWriteSet changes, CancellationToken token)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IProjectionApplyUnitOfWork>().ApplyAsync(changes, token);
    }

    public async Task InstallAsync(RevisionImage image, Guid snapshotId, string snapshotDigest, CancellationToken token)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SnapshotProjectionUnitOfWork>()
            .InstallAsync(image, options.Value.NodeId, snapshotId, snapshotDigest, token);
    }

    public async Task VerifyAsync(RevisionImage image, CancellationToken token)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SnapshotProjectionUnitOfWork>()
            .VerifyRecoveryAsync(image, options.Value.NodeId, token);
    }

    public async Task<RevisionImage> ExportAsync(RevisionImage image, CancellationToken token)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SnapshotProjectionUnitOfWork>()
            .ExportAsync(image, options.Value.NodeId, token);
    }

    public async Task CheckpointAsync(Guid snapshotId, string digest, long index, CancellationToken token)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        SnapshotProjectionUnitOfWork work = scope.ServiceProvider.GetRequiredService<SnapshotProjectionUnitOfWork>();
        await work.ExecuteInTransactionAsync(cancellation => work.RecordCheckpointAsync(
            options.Value.NodeId, snapshotId, digest, index, cancellation), token);
    }
}
