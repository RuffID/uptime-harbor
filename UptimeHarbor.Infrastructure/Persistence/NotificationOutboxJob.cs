using System.Text.Json;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Monitoring.Models;

namespace UptimeHarbor.Infrastructure.Persistence;

public class NotificationOutboxJob
{
    private NotificationOutboxJob()
    {
    }

    private NotificationOutboxJob(
        Guid id,
        string payloadJson,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        PayloadJson = payloadJson;
        CreatedAtUtc = createdAtUtc;
        NextAttemptAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public string PayloadJson { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset NextAttemptAtUtc { get; private set; }

    public DateTimeOffset? LockedUntilUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public DateTimeOffset? FailedAtUtc { get; private set; }

    public static NotificationOutboxJob Create(MonitoringStateChangedEvent monitoringEvent)
    {
        ArgumentNullException.ThrowIfNull(monitoringEvent);
        return new NotificationOutboxJob(
            Guid.NewGuid(),
            JsonSerializer.Serialize(NotificationOutboxPayload.Create(monitoringEvent)),
            monitoringEvent.CheckedAtUtc);
    }

    public MonitoringStateChangedEvent ReadEvent()
    {
        return ReadPayload().MonitoringEvent;
    }

    public string? ReadCancellationReason()
    {
        return ReadPayload().Cancellation?.Reason;
    }

    public DateTimeOffset? ReadCancelledAtUtc()
    {
        return ReadPayload().Cancellation?.CancelledAtUtc;
    }

    public void Cancel(
        DateTimeOffset nowUtc,
        RecoveryNotificationCancellationReason reason)
    {
        EnsureUtc(nowUtc, nameof(nowUtc));
        if (CompletedAtUtc is not null || FailedAtUtc is not null)
        {
            return;
        }

        NotificationOutboxPayload payload = ReadPayload();
        payload.Cancellation = new NotificationOutboxCancellation(
            nowUtc,
            reason.ToString());
        PayloadJson = JsonSerializer.Serialize(payload);
        CompletedAtUtc = nowUtc;
        LockedUntilUtc = null;
    }

    public void StartAttempt(DateTimeOffset nowUtc, TimeSpan leaseDuration)
    {
        EnsureUtc(nowUtc, nameof(nowUtc));
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        AttemptCount++;
        LockedUntilUtc = nowUtc.Add(leaseDuration);
    }

    public void Complete(DateTimeOffset nowUtc)
    {
        EnsureUtc(nowUtc, nameof(nowUtc));
        CompletedAtUtc = nowUtc;
        LockedUntilUtc = null;
    }

    public void ScheduleRetry(DateTimeOffset nowUtc, TimeSpan delay)
    {
        EnsureUtc(nowUtc, nameof(nowUtc));
        if (delay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay));
        }

        NextAttemptAtUtc = nowUtc.Add(delay);
        LockedUntilUtc = null;
    }

    public void Fail(DateTimeOffset nowUtc)
    {
        EnsureUtc(nowUtc, nameof(nowUtc));
        FailedAtUtc = nowUtc;
        LockedUntilUtc = null;
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Дата и время должны быть указаны в UTC.", parameterName);
        }
    }

    private NotificationOutboxPayload ReadPayload()
    {
        using JsonDocument document = JsonDocument.Parse(PayloadJson);
        if (document.RootElement.TryGetProperty(
            nameof(NotificationOutboxPayload.MonitoringEvent),
            out _))
        {
            return JsonSerializer.Deserialize<NotificationOutboxPayload>(PayloadJson)
                ?? throw new InvalidOperationException("Outbox payload не содержит данные задания.");
        }

        MonitoringStateChangedEvent monitoringEvent =
            JsonSerializer.Deserialize<MonitoringStateChangedEvent>(PayloadJson)
            ?? throw new InvalidOperationException("Outbox payload не содержит событие мониторинга.");
        return NotificationOutboxPayload.Create(monitoringEvent);
    }

    private class NotificationOutboxPayload
    {
        public MonitoringStateChangedEvent MonitoringEvent { get; init; } = null!;

        public NotificationOutboxCancellation? Cancellation { get; set; }

        public static NotificationOutboxPayload Create(
            MonitoringStateChangedEvent monitoringEvent)
        {
            return new NotificationOutboxPayload
            {
                MonitoringEvent = monitoringEvent
            };
        }
    }

    private record NotificationOutboxCancellation(
        DateTimeOffset CancelledAtUtc,
        string Reason);
}
