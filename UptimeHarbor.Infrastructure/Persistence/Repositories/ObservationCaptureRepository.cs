using EFCoreLibrary.Abstractions.Database.Repository.Base;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Repositories;

public class ObservationCaptureRepository(
    IContextGetItemByPredicateRepository<ObservationCaptureCursor, UptimeHarborDbContext> reader,
    IContextCreateItemRepository<ObservationCaptureCursor, UptimeHarborDbContext> streamWriter,
    IContextCreateItemRepository<CapturedObservation, UptimeHarborDbContext> observationWriter)
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
}
