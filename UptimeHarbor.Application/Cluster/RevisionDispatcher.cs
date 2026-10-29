using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster;

public class RevisionDispatcher(IConsensusWriter consensus, RevisionStateStore accepted, IClusterStateView? view = null,
    IMonitoringAudit? monitoringAudit = null, TimeProvider? time = null)
{
    private readonly SemaphoreSlim serial = new(1, 1);

    public async Task<CommandResult> AcceptAsync(RevisionCommand command, Guid trustedOriginNodeId,
        CancellationToken cancellationToken)
    {
        await serial.WaitAsync(cancellationToken);
        try
        {
            return await AcceptCoreAsync(command, trustedOriginNodeId, cancellationToken);
        }
        finally
        {
            serial.Release();
        }
    }

    private async Task<CommandResult> AcceptCoreAsync(RevisionCommand command, Guid trustedOriginNodeId,
        CancellationToken cancellationToken)
    {
        if (command.OriginNodeId != trustedOriginNodeId || !accepted.Available)
        {
            throw new UnauthorizedAccessException("UnconfirmedOriginOrProjection");
        }

        byte[] payload = RevisionCodec.Encode(command);
        RevisionImage image = accepted.Current!;
        if (command.ClusterId != image.ClusterId || command.ClusterGeneration != image.Generation)
        {
            throw new InvalidDataException("ClusterMismatch");
        }

        if (MonitoringCommandValidation.IsSystem(command))
        {
            MonitoringCommand monitoring = command.Monitoring!;
            if (!image.Monitoring.Nodes.TryGetValue(trustedOriginNodeId, out Guid storage)
                || storage != monitoring.StorageGeneration)
            {
                throw new UnauthorizedAccessException("ObserverNotAdmitted");
            }

            if (command.Action != "RecordObservations")
            {
                ConsensusStatus? status = view?.Consensus;
                bool coordinator = status is not null && status.IsLeader && status.NodeId == trustedOriginNodeId
                    && status.Term == monitoring.CoordinatorTerm && !status.LeadershipToken.IsCancellationRequested
                    && !status.ConsensusToken.IsCancellationRequested && view!.Accepted.CaughtUp;
                if (!coordinator)
                {
                    throw new UnauthorizedAccessException("CoordinatorNotConfirmed");
                }
            }
            else if (monitoring.Observations.Any(item => item.CheckedAtUtc > (time ?? TimeProvider.System).GetUtcNow().AddSeconds(5)))
            {
                throw new InvalidDataException("ObservationClockAhead");
            }
        }

        if (image.Results.TryGetValue(command.OperationId, out CommandResult? previous))
        {
            if (previous.Digest != RevisionCodec.Digest(payload))
            {
                throw new InvalidDataException("IdempotencyKeyReuse");
            }

            return previous;
        }

        // Окончательная повторная авторизация выполняется reducer на позиции принятия.
        RevisionImage before = image;
        await consensus.ReplicateAsync(payload, RevisionCodec.COMMAND_ID, cancellationToken);
        image = accepted.Current ?? throw new InvalidOperationException("ProjectionUnavailable");
        if (!accepted.Available || !image.Results.TryGetValue(command.OperationId, out CommandResult? result))
        {
            throw new InvalidOperationException("DecisionUnavailable");
        }

        foreach (MonitoringEvent acceptedEvent in image.Monitoring.Events.Values.Where(item => item.Index == result.Index
            && !before.Monitoring.Events.ContainsKey(item.Id)))
        {
            MonitoringIncident incident = image.Monitoring.Incidents[acceptedEvent.IncidentId];
            monitoringAudit?.Transition(command.OriginNodeId, incident.Id, acceptedEvent.Id, incident.TargetId, result.Index);
        }

        return result;
    }
}
