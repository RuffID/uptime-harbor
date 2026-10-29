using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster;

public class RevisionDispatcher(IConsensusWriter consensus, RevisionStateStore accepted)
{
    public async Task<CommandResult> AcceptAsync(RevisionCommand command, Guid trustedOriginNodeId,
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

        if (image.Results.TryGetValue(command.OperationId, out CommandResult? previous))
        {
            if (previous.Digest != RevisionCodec.Digest(payload))
            {
                throw new InvalidDataException("IdempotencyKeyReuse");
            }

            return previous;
        }

        // Окончательная повторная авторизация выполняется reducer на позиции принятия.
        await consensus.ReplicateAsync(payload, RevisionCodec.COMMAND_ID, cancellationToken);
        image = accepted.Current ?? throw new InvalidOperationException("ProjectionUnavailable");
        if (!accepted.Available || !image.Results.TryGetValue(command.OperationId, out CommandResult? result))
        {
            throw new InvalidOperationException("DecisionUnavailable");
        }

        return result;
    }
}
