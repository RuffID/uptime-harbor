using UptimeHarbor.Application.Monitoring;

namespace UptimeHarbor.Web.Hubs.Models;

public record MonitoringObjectChangedMessage(
    Guid ObjectId,
    MonitoringObjectKind ObjectKind);
