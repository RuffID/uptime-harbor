using System.Globalization;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Monitoring.Models;
using UptimeHarbor.Application.Notifications.Models;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Domain.Notifications;

namespace UptimeHarbor.Application.Notifications;

public class NotificationDeliveryService(
    INotificationSettingsUnitOfWork unitOfWork,
    TimeProvider timeProvider)
    : INotificationDeliveryService
{
    public async Task<IReadOnlyList<PendingNotificationDeliveryDto>> GetPendingDeliveriesAsync(
        MonitoringStateChangedEvent monitoringEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(monitoringEvent);
        EnsureUtc(monitoringEvent.CheckedAtUtc, nameof(monitoringEvent));
        if (monitoringEvent.CurrentIncidentId is not Guid incidentId)
        {
            return [];
        }

        NotificationDeliveryKind? kind = monitoringEvent.Status switch
        {
            MonitoringStatus.Unavailable => NotificationDeliveryKind.Outage,
            MonitoringStatus.Available => NotificationDeliveryKind.Recovery,
            MonitoringStatus.Unknown => null,
            _ => throw new ArgumentOutOfRangeException(
                nameof(monitoringEvent.Status),
                monitoringEvent.Status,
                "Неизвестный статус мониторинга.")
        };
        if (kind is null)
        {
            return [];
        }

        List<NotificationBinding> bindings = monitoringEvent.ObjectKind switch
        {
            MonitoringObjectKind.Server => await unitOfWork.NotificationBindings.GetByServerIdAsync(
                monitoringEvent.ObjectId,
                cancellationToken),
            MonitoringObjectKind.Container => await unitOfWork.NotificationBindings.GetByContainerIdAsync(
                monitoringEvent.ObjectId,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(monitoringEvent.ObjectKind),
                monitoringEvent.ObjectKind,
                "Неизвестный тип объекта мониторинга.")
        };
        IEnumerable<NotificationBinding> pendingBindings = kind == NotificationDeliveryKind.Outage
            ? bindings.Where(binding => binding.RequiresOutageDelivery(incidentId))
            : bindings.Where(binding => binding.RequiresRecoveryDelivery(incidentId));
        Dictionary<Guid, TelegramChat> chats = (await unitOfWork.TelegramChats.GetAllAsync(cancellationToken))
            .ToDictionary(chat => chat.Id);

        string? parentServerName = null;
        if (monitoringEvent.ObjectKind == MonitoringObjectKind.Container)
        {
            Guid serverId = monitoringEvent.ServerId
                ?? throw new InvalidOperationException("Событие контейнера должно содержать родительский сервер.");
            MonitoredServer? server = await unitOfWork.MonitoredServers.GetItemByIdAsync(
                serverId,
                cancellationToken);
            parentServerName = server is null
                ? ShortGuid(serverId)
                : GetDisplayName(server.LastKnownName, server.Id);
        }

        string message = CreateMessage(monitoringEvent, kind.Value, parentServerName);
        List<PendingNotificationDeliveryDto> deliveries = [];
        foreach (NotificationBinding binding in pendingBindings)
        {
            if (!chats.TryGetValue(binding.TelegramChatId, out TelegramChat? chat))
            {
                throw new InvalidOperationException("Привязка уведомления ссылается на отсутствующий чат.");
            }

            deliveries.Add(new PendingNotificationDeliveryDto(
                binding.Id,
                incidentId,
                kind.Value,
                new NotificationRecipient(
                    chat.Id,
                    chat.TelegramChatId.ToString(CultureInfo.InvariantCulture)),
                message));
        }

        return deliveries;
    }

    public async Task<ServiceResult> MarkDeliverySuccessfulAsync(
        Guid bindingId,
        Guid incidentId,
        NotificationDeliveryKind kind,
        CancellationToken cancellationToken = default)
    {
        NotificationBinding? binding = await unitOfWork.NotificationBindings.GetItemByIdAsync(
            bindingId,
            cancellationToken);
        if (binding is null)
        {
            return ServiceResult.Fail(ServiceErrorType.NotFound, "Привязка уведомления не найдена.");
        }

        if (kind == NotificationDeliveryKind.Outage)
        {
            if (!binding.RequiresOutageDelivery(incidentId))
            {
                return ServiceResult.Ok();
            }

            binding.MarkOutageDelivered(incidentId);
        }
        else if (kind == NotificationDeliveryKind.Recovery)
        {
            if (!binding.RequiresRecoveryDelivery(incidentId))
            {
                return binding.RecoveryDeliveredIncidentId == incidentId
                    ? ServiceResult.Ok()
                    : ServiceResult.Fail(
                        ServiceErrorType.Conflict,
                        "Для привязки нет успешной outage-доставки этого инцидента.");
            }

            binding.MarkRecoveryDelivered(incidentId);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Неизвестный тип доставки.");
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    private string CreateMessage(
        MonitoringStateChangedEvent monitoringEvent,
        NotificationDeliveryKind kind,
        string? parentServerName)
    {
        List<string> lines =
        [
            $"Объект: {(monitoringEvent.ObjectKind == MonitoringObjectKind.Server ? "Сервер" : "Контейнер")}",
            $"Имя: {GetDisplayName(monitoringEvent.Name, monitoringEvent.ObjectId)}"
        ];
        if (!string.IsNullOrWhiteSpace(monitoringEvent.Description))
        {
            lines.Add($"Описание: {monitoringEvent.Description}");
        }

        if (parentServerName is not null)
        {
            lines.Add($"Родительский сервер: {parentServerName}");
        }

        lines.Add($"Статус: {(kind == NotificationDeliveryKind.Outage ? "Недоступен" : "Доступен")}");
        lines.Add($"Время события (UTC): {monitoringEvent.CheckedAtUtc:O}");

        if (kind == NotificationDeliveryKind.Recovery
            && monitoringEvent.ObjectKind == MonitoringObjectKind.Container
            && monitoringEvent.StartedAtUtc is DateTimeOffset startedAtUtc)
        {
            EnsureUtc(startedAtUtc, nameof(monitoringEvent));
            DateTimeOffset nowUtc = timeProvider.GetUtcNow();
            EnsureUtc(nowUtc, nameof(timeProvider));
            TimeSpan uptime = nowUtc - startedAtUtc;
            if (uptime < TimeSpan.Zero)
            {
                throw new InvalidOperationException("Время запуска контейнера не может быть позже текущего UTC-времени.");
            }

            lines.Add($"Запущен (UTC): {startedAtUtc:O}");
            lines.Add($"Uptime: {uptime:c}");
        }

        return string.Join('\n', lines);
    }

    private static string GetDisplayName(string? name, Guid id) => name ?? ShortGuid(id);

    private static string ShortGuid(Guid id) => id.ToString("N")[..8];

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Дата и время должны быть указаны в UTC.", parameterName);
        }
    }
}
