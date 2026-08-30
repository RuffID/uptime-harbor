using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Visibility.Models;

namespace UptimeHarbor.Application.Visibility;

public interface IMonitoringRecipientReadQuery
{
    Task<IReadOnlyList<MonitoringRecipientReadRecord>> GetCandidatesAsync(
        MonitoringObjectKind objectKind,
        CancellationToken cancellationToken = default);
}
