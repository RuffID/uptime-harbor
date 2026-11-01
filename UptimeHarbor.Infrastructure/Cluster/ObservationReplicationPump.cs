using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Bounded страницы local capture. ACK берётся только из accepted frontier, очередь не удаляется.</summary>
public class ObservationReplicationPump(IServiceScopeFactory scopes, RevisionStateStore accepted,
    IRevisionTransport transport, IOptions<ClusterOptions> options)
{
    private Guid? afterStream;

    public async Task PumpAsync(CancellationToken token)
    {
        if (!options.Value.Enabled || !accepted.Available)
        {
            return;
        }

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IObservationCaptureRepository repository = scope.ServiceProvider.GetRequiredService<IObservationCaptureRepository>();
        IReadOnlyList<ObservationCaptureCursor> streams = await repository.StreamsAsync(options.Value.NodeId, afterStream, 100, token);
        if (streams.Count == 0)
        {
            afterStream = null;
            return;
        }

        foreach (ObservationCaptureCursor stream in streams)
        {
            if (stream.StorageGeneration != options.Value.StorageGeneration)
            {
                afterStream = stream.Id;
                continue;
            }

            RevisionImage image = accepted.Current ?? throw new InvalidOperationException("ProjectionUnavailable");
            long through = image.Monitoring.Streams.GetValueOrDefault(stream.Id)?.Sequence ?? 0;
            IReadOnlyList<CapturedObservation> page = await repository.ReadAsync(stream.Id, through, 100, token);
            if (page.Count == 0)
            {
                afterStream = stream.Id;
                continue;
            }

            ImmutableArray<MonitoringObservation>.Builder observations = ImmutableArray.CreateBuilder<MonitoringObservation>();
            foreach (CapturedObservation captured in page)
            {
                MonitoringObservation observation = JsonSerializer.Deserialize<MonitoringObservation>(captured.Payload)
                    ?? throw new InvalidDataException("CapturedObservationMissing");
                if (MonitoringKeys.Digest(observation) != captured.Digest || observation.Id != captured.Id
                    || observation.Sequence != captured.Sequence || observation.ObserverNodeId != options.Value.NodeId
                    || observation.StorageGeneration != options.Value.StorageGeneration)
                {
                    throw new InvalidDataException("CapturedObservationMismatch");
                }

                observations.Add(observation);
            }

            // Устойчивый ID пакета и время не меняются при retry/restart.
            RevisionCommand command = MonitoringCommands.Record(image, options.Value.NodeId,
                options.Value.StorageGeneration, observations.ToImmutable());
            while (RevisionCodec.Encode(command).Length > options.Value.CommandMaxBytes && observations.Count > 1)
            {
                observations.RemoveAt(observations.Count - 1);
                command = MonitoringCommands.Record(image, options.Value.NodeId, options.Value.StorageGeneration, observations.ToImmutable());
            }

            IReadOnlyList<CommandResult> results = await transport.SendAsync([command], token);
            PendingReplicationPump.ValidateResults([command], results);
            if (results.Single().Outcome != OperationOutcome.Committed)
            {
                throw new InvalidOperationException("ObservationRangeNotAccepted");
            }

            afterStream = stream.Id;
        }
    }
}
