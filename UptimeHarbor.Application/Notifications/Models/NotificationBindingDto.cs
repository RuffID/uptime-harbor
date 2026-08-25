namespace UptimeHarbor.Application.Notifications.Models;

public record NotificationBindingDto(
    Guid Id,
    Guid TelegramChatId,
    Guid? ServerId,
    Guid? ContainerId);
