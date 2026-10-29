using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Notifications;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Follower сохраняет пользовательскую заявку; существующий Pending pump передаёт её главному.</summary>
public class ClusterTestDelivery(PendingCommandService pending, RevisionStateStore accepted,
    IOptions<ClusterOptions> options, TimeProvider time) : IAcceptedTestDelivery
{
    public async Task<ServiceResult> SubmitAsync(Guid chatId, CancellationToken token)
    {
        if (!accepted.Available || accepted.Current is null || !options.Value.Enabled)
        {
            return ServiceResult.Fail(ServiceErrorType.Conflict, "Принятое состояние пока недоступно.");
        }

        RevisionImage image = accepted.Current;
        RevisionCommand command = new()
        {
            ClusterId = image.ClusterId, ClusterGeneration = image.Generation, OperationId = Guid.NewGuid(),
            OriginNodeId = options.Value.NodeId, ActorUserId = Guid.Empty, ActorRevision = 0,
            EntityId = chatId, EntityKind = DeletedEntityKind.Chat, BaseRevision = 0,
            Action = "TestDelivery", CreatedAtUtc = time.GetUtcNow()
        };
        await pending.SubmitAsync(command, options.Value.NodeId, token);
        return ServiceResult.Ok();
    }
}
