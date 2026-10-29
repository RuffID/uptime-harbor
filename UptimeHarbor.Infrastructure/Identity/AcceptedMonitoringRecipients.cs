using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Application.Visibility;
using UptimeHarbor.Application.Visibility.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Identity;

public class AcceptedMonitoringRecipients(RevisionStateStore state) : IMonitoringRecipientReadQuery
{
    public Task<IReadOnlyList<MonitoringRecipientReadRecord>> GetCandidatesAsync(MonitoringObjectKind objectKind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!state.Available)
        {
            return Task.FromResult<IReadOnlyList<MonitoringRecipientReadRecord>>([]);
        }

        IReadOnlyList<MonitoringRecipientReadRecord> recipients = state.Current!.Values.Values
            .Where(value => value.Kind == DeletedEntityKind.Account
                && AcceptedAdministratorAccessPolicy.IsActiveAdministrator(state.Find(value.Id)))
            .Select(value => new MonitoringRecipientReadRecord(value.Id, true, true, [], [])).ToArray();
        return Task.FromResult(recipients);
    }
}
