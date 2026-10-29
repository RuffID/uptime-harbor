using EFCoreLibrary.Abstractions.Database.Repository.Base;
using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Repositories;

public class ObservationCaptureRepository(
    IContextGetItemByPredicateRepository<ObservationCaptureCursor, UptimeHarborDbContext> reader,
    IContextCreateItemRepository<ObservationCaptureCursor, UptimeHarborDbContext> streamWriter,
    IContextCreateItemRepository<CapturedObservation, UptimeHarborDbContext> observationWriter,
    UptimeHarborDbContext context)
    : IObservationCaptureRepository
{
    public Task<ObservationCaptureCursor?> GetStreamAsync(Guid streamId, CancellationToken cancellationToken = default)
    {
        return reader.GetItemByPredicateAsync(item => item.Id == streamId, asNoTracking: false, ct: cancellationToken);
    }

    public void CreateStream(ObservationCaptureCursor stream)
    {
        streamWriter.Create(stream);
    }

    public void Create(CapturedObservation observation)
    {
        observationWriter.Create(observation);
    }

    public Task<CapturedObservation?> FindAsync(Guid observationId, CancellationToken cancellationToken = default)
    {
        return context.Set<CapturedObservation>().AsNoTracking().SingleOrDefaultAsync(item => item.Id == observationId, cancellationToken);
    }

    public async Task<IReadOnlyList<ObservationCaptureCursor>> StreamsAsync(Guid observerNodeId, Guid? afterStreamId,
        int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
        IQueryable<ObservationCaptureCursor> query = context.Set<ObservationCaptureCursor>().AsNoTracking()
            .Where(item => item.ObserverNodeId == observerNodeId);
        if (afterStreamId.HasValue)
        {
            query = query.Where(item => item.Id.CompareTo(afterStreamId.Value) > 0);
        }

        return await query.OrderBy(item => item.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CapturedObservation>> ReadAsync(Guid streamId, long afterSequence, int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
        return await context.Set<CapturedObservation>().AsNoTracking()
            .Where(item => item.StreamId == streamId && item.Sequence > afterSequence)
            .OrderBy(item => item.Sequence).Take(limit).ToListAsync(cancellationToken);
    }
}
