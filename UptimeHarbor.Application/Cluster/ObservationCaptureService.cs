using System.Text.Json;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

public class ObservationCaptureService(IObservationCaptureUnitOfWork work, ObservationCaptureGate gate,
    RevisionStateStore accepted)
{
    public async Task<CapturedObservation?> CaptureAsync(MonitoringObservation result, CancellationToken token)
    {
        await gate.Serial.WaitAsync(token);
        try
        {
            CapturedObservation? captured = null;
            await work.ExecuteInTransactionAsync(async cancellation =>
            {
                CapturedObservation? prior = await work.Observations.FindAsync(result.Id, cancellation);
                if (prior is not null)
                {
                    MonitoringObservation stored = JsonSerializer.Deserialize<MonitoringObservation>(prior.Payload)!;
                    if (stored != result with { Sequence = stored.Sequence })
                    {
                        throw new InvalidDataException("ObservationIdReuse");
                    }

                    captured = prior;
                    return;
                }

                RevisionImage? image = accepted.Current;
                MonitoringTarget? current = null;
                if (accepted.Available && image is not null)
                {
                    current = MonitoringTargets.Find(image, result.TargetId);
                }

                bool validTarget = current is not null && current.ConfigRevision == result.ConfigRevision
                    && current.CheckGeneration == result.CheckGeneration && current.Observers.Contains(result.ObserverNodeId);
                bool validOrigin = image is not null && image.Monitoring.Nodes.GetValueOrDefault(result.ObserverNodeId) == result.StorageGeneration;
                if (!validTarget || !validOrigin)
                {
                    return;
                }

                Guid streamId = MonitoringKeys.Stream(result.TargetId, result.ObserverNodeId,
                    result.StorageGeneration, result.CheckGeneration);
                ObservationCaptureCursor? cursor = await work.Observations.GetStreamAsync(streamId, cancellation);
                if (cursor is null)
                {
                    cursor = new(streamId, result.TargetId, result.ObserverNodeId, result.StorageGeneration, result.CheckGeneration);
                    work.Observations.CreateStream(cursor);
                }

                MonitoringObservation observation = result with { Sequence = checked(cursor.LastSequence + 1) };
                MonitoringCommandValidation.ValidateObservation(observation);
                string payload = JsonSerializer.Serialize(observation);
                captured = cursor.Capture(observation.Id, MonitoringKeys.Digest(observation), payload, observation.CheckedAtUtc);
                work.Observations.Create(captured);
            }, token);

            return captured;
        }
        finally
        {
            gate.Serial.Release();
        }
    }
}
