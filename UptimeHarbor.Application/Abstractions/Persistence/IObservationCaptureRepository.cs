using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Abstractions.Persistence;

public interface IObservationCaptureRepository
{
    Task<ObservationCaptureCursor?> GetStreamAsync(Guid streamId, CancellationToken cancellationToken = default);
    void CreateStream(ObservationCaptureCursor stream);
    void Create(CapturedObservation observation);
    Task<CapturedObservation?> FindAsync(Guid observationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ObservationCaptureCursor>> StreamsAsync(Guid observerNodeId, Guid? afterStreamId, int limit,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CapturedObservation>> ReadAsync(Guid streamId, long afterSequence, int limit,
        CancellationToken cancellationToken = default);
}
