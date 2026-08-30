using UptimeHarbor.Application.Monitoring.Models;

namespace UptimeHarbor.Infrastructure.Telegram;

public record ClaimedNotificationOutboxJob(
    Guid Id,
    int AttemptCount,
    MonitoringStateChangedEvent MonitoringEvent);
