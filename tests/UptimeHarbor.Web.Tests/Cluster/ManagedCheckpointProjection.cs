using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

/// <summary>Изолированная SQL-проекция с управляемыми границами checkpoint, без подключения к БД.</summary>
public class ManagedCheckpointProjection : IRevisionProjectionStore
{
    public ProjectionHead? Head { get; set; }
    public Func<CancellationToken, Task>? BeforeCheckpoint { get; set; }
    public Func<CancellationToken, Task>? AfterCheckpoint { get; set; }
    public Guid CheckpointId { get; private set; }
    public long CheckpointIndex { get; private set; }
    public int Exports { get; private set; }
    public int Checkpoints { get; private set; }

    public Task ApplyAsync(ProjectionWriteSet changes, CancellationToken token)
    {
        Assert.Equal(Head?.Index ?? 0, changes.ExpectedCursorIndex);
        Head = new(changes.Receipt.ClusterId, changes.Receipt.ClusterGeneration,
            changes.Receipt.LogIndex, changes.Receipt.Term, changes.Receipt.Digest);
        return Task.CompletedTask;
    }

    public Task<ProjectionHead?> ReadHeadAsync(CancellationToken token)
    {
        return Task.FromResult(Head);
    }

    public Task VerifyAsync(RevisionImage image, CancellationToken token)
    {
        Assert.Equal(image.Index, Head?.Index ?? 0);
        Assert.Equal(image.Digest, Head?.Digest);
        return Task.CompletedTask;
    }

    public Task InstallAsync(RevisionImage image, Guid snapshotId, string snapshotDigest, CancellationToken token)
    {
        throw new NotSupportedException();
    }

    public Task<RevisionImage> ExportAsync(RevisionImage image, CancellationToken token)
    {
        Assert.Equal(image.Index, Head!.Index);
        Assert.Equal(image.Digest, Head.Digest);
        Exports++;
        return Task.FromResult(image);
    }

    public async Task CheckpointAsync(Guid snapshotId, string digest, long index, CancellationToken token)
    {
        if (BeforeCheckpoint is not null)
        {
            await BeforeCheckpoint(token);
        }

        Assert.True(index <= Head!.Index);
        Assert.True(index >= CheckpointIndex);
        CheckpointId = snapshotId;
        CheckpointIndex = index;
        Checkpoints++;
        if (AfterCheckpoint is not null)
        {
            await AfterCheckpoint(token);
        }
    }
}
