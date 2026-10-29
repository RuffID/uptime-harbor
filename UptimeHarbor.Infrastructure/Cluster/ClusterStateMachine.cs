using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster;

public class ClusterStateMachine(IClusterRecovery recovery, NodeReadiness readiness) : IStateMachine
{
    private bool restored;
    private volatile bool failed;
    public int Version => 1;
    public bool Restored => restored;
    public bool Failed => failed;

    public ISnapshot? Snapshot
    {
        get
        {
            if (!restored)
            {
                throw new InvalidOperationException("Restore checkpoint before creating WAL.");
            }

            ClusterSnapshot? snapshot = recovery.Snapshot;
            if (snapshot is null)
            {
                return null;
            }

            return new RestoredRaftSnapshot(snapshot);
        }
    }

    public async ValueTask<bool> RestoreAsync(ClusterOptions options, CancellationToken cancellationToken)
    {
        restored = await recovery.RestoreAsync(options.ClusterId, options.Generation,
            options.NodeId, options.StorageGeneration, cancellationToken);
        if (restored)
        {
            readiness.Report("Projection", false, "Unknown", TimeSpan.MaxValue);
        }

        return restored;
    }

    public void ConfirmReplay(bool confirmed)
    {
        if (!restored || !confirmed)
        {
            return;
        }

        failed = false;
        readiness.Report("Projection", true, "Ready", TimeSpan.MaxValue);
    }

    public async ValueTask<long> ApplyAsync(LogEntry entry, CancellationToken token)
    {
        if (!restored)
        {
            throw new InvalidOperationException("Cluster checkpoint is not restored.");
        }

        try
        {
            if (!entry.IsSnapshot && !entry.IsConfiguration && entry.Length > 262144)
            {
                throw new InvalidDataException("Committed application entry exceeds the protocol limit.");
            }

            CommittedRecord record = new(entry.Index, entry.Term, entry.IsConfiguration, entry.IsSnapshot,
                (stream, cancellation) => entry.WriteToAsync(IAsyncBinaryWriter.Create(stream, new byte[4096]), cancellation));
            long applied = await recovery.ApplyAsync(record, token);
            if (applied < entry.Index)
            {
                throw new InvalidDataException("Projection did not apply the committed entry.");
            }

            if (entry.Context is TaskCompletionSource<long> completion)
            {
                completion.TrySetResult(entry.Index);
            }

            return applied;
        }
        catch (Exception exception)
        {
            failed = true;
            readiness.Report("Projection", false, "ProjectionUnavailable", TimeSpan.MaxValue);
            readiness.Report("Database", false, "DatabaseUnavailable", TimeSpan.FromSeconds(10));
            Exception safeFailure = new InvalidOperationException("Cluster projection apply failed.");
            if (exception is OperationCanceledException && token.IsCancellationRequested)
            {
                safeFailure = new OperationCanceledException("Cluster projection apply was cancelled.", token);
            }

            if (entry.Context is TaskCompletionSource<long> completion)
            {
                completion.TrySetException(safeFailure);
            }

            // DotNext и HTTP middleware не должны получить сообщение reducer/provider с payload.
            throw safeFailure;
        }
    }

    public async ValueTask ReclaimGarbageAsync(long watermark, CancellationToken token)
    {
        try
        {
            await recovery.ReclaimAsync(watermark, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException("Cluster retention was cancelled.", token);
        }
        catch
        {
            throw new InvalidOperationException("Cluster retention failed.");
        }
    }
}
