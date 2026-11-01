using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster;

public class UnavailableSenderConsensus : ISenderConsensus
{
    public ValueTask<AuthorityDecision?> RenewAsync(SenderAuthorityKey key, Guid roundId, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult<AuthorityDecision?>(null);
    }

    public ValueTask<DeliveryAttemptPermit?> BeginAttemptAsync(SenderAuthorityKey key, Guid attemptId, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult<DeliveryAttemptPermit?>(null);
    }
}
