using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster;

/// <summary>Принятые и применённые решения будущего reducer/delivery ledger, а не локальная lease.</summary>
public interface ISenderConsensus
{
    ValueTask<AuthorityDecision?> RenewAsync(SenderAuthorityKey key, Guid roundId, CancellationToken cancellationToken);
    ValueTask<DeliveryAttemptPermit?> BeginAttemptAsync(SenderAuthorityKey key, Guid attemptId, CancellationToken cancellationToken);
}
