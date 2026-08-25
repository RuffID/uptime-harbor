namespace UptimeHarbor.Application.Notifications.Models;

public record NotificationSettingsDto(
    IReadOnlyList<TelegramChatDto> Chats,
    IReadOnlyList<NotificationBindingDto> Bindings,
    IReadOnlyList<NotificationTargetDto> Targets);
