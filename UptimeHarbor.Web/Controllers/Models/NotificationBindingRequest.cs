using UptimeHarbor.Application.Monitoring;

namespace UptimeHarbor.Web.Controllers.Models;

public class NotificationBindingRequest
{
    public Guid ChatId { get; init; }

    public MonitoringObjectKind ObjectKind { get; init; }

    public Guid ObjectId { get; init; }
}
