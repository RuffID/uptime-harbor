using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Abstractions.Persistence;

public interface IObservationCaptureRepository
{
    Task<ObservationCaptureCursor?> GetStreamAsync(Guid streamId, CancellationToken cancellationToken = default);
    void CreateStream(ObservationCaptureCursor stream);
    void Create(CapturedObservation observation);
}
