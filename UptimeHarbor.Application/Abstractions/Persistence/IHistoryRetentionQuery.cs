using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Abstractions.Persistence;

public interface IHistoryRetentionQuery
{
    Task<HistoryProtection> GetIncidentProtectionAsync(Guid incidentId, CancellationToken cancellationToken = default);
}
