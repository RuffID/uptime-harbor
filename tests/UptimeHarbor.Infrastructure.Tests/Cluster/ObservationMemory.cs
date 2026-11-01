using Microsoft.EntityFrameworkCore.Storage;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Persistence.UnitOfWork;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

/// <summary>Транзакционный double. Не доказывает SQL/provider locking или fsync.</summary>
internal class ObservationMemory : IObservationCaptureRepository, IUptimeHarborDbContextSession
{
    public List<CapturedObservation> Saved { get; private set; } = [];
    private List<CapturedObservation> working = [];
    private Dictionary<Guid, ObservationCaptureCursor> cursors = [];
    public bool FailSave { get; set; }
    public bool LoseCommitResponse { get; set; }

    public ObservationMemory(IEnumerable<CapturedObservation>? restored = null)
    {
        if (restored is not null)
        {
            Saved = restored.ToList();
        }

        Begin();
    }

    private void Begin()
    {
        working = Saved.ToList();
        cursors = [];
        foreach (CapturedObservation captured in working.OrderBy(item => item.Sequence))
        {
            var fact = System.Text.Json.JsonSerializer.Deserialize<UptimeHarbor.Application.Cluster.Models.MonitoringObservation>(captured.Payload)!;
            if (!cursors.TryGetValue(captured.StreamId, out ObservationCaptureCursor? cursor))
            {
                cursor = new(captured.StreamId, fact.TargetId, fact.ObserverNodeId, fact.StorageGeneration, fact.CheckGeneration);
                cursors.Add(cursor.Id, cursor);
            }

            cursor.Capture(captured.Id, captured.Digest, captured.Payload, captured.CheckedAtUtc);
        }
    }

    public Task<ObservationCaptureCursor?> GetStreamAsync(Guid streamId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(cursors.GetValueOrDefault(streamId));
    }

    public void CreateStream(ObservationCaptureCursor stream)
    {
        cursors.Add(stream.Id, stream);
    }

    public void Create(CapturedObservation observation)
    {
        working.Add(observation);
    }

    public Task<CapturedObservation?> FindAsync(Guid observationId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(working.SingleOrDefault(item => item.Id == observationId));
    }

    public Task<IReadOnlyList<ObservationCaptureCursor>> StreamsAsync(Guid observerNodeId, Guid? afterStreamId, int limit,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ObservationCaptureCursor> page = cursors.Values.Where(item => item.ObserverNodeId == observerNodeId
            && (!afterStreamId.HasValue || item.Id.CompareTo(afterStreamId.Value) > 0)).OrderBy(item => item.Id).Take(limit).ToArray();
        return Task.FromResult(page);
    }

    public Task<IReadOnlyList<CapturedObservation>> ReadAsync(Guid streamId, long afterSequence, int limit,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CapturedObservation> page = Saved.Where(item => item.StreamId == streamId && item.Sequence > afterSequence)
            .OrderBy(item => item.Sequence).Take(limit).ToArray();
        return Task.FromResult(page);
    }

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        Begin();
        return Task.FromResult<IDbContextTransaction>(new Transaction(this));
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (FailSave)
        {
            throw new InvalidOperationException("InjectedSaveFailure");
        }

        return Task.FromResult(working.Count);
    }

    public void Clear()
    {
        Begin();
    }

    private class Transaction(ObservationMemory owner) : IDbContextTransaction
    {
        public Guid TransactionId { get; } = Guid.NewGuid();

        public void Commit()
        {
            owner.Saved = owner.working.ToList();
            if (owner.LoseCommitResponse)
            {
                throw new IOException("InjectedCommitResponseLoss");
            }
        }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            Commit();
            return Task.CompletedTask;
        }

        public void Rollback()
        {
            owner.Begin();
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            Rollback();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
