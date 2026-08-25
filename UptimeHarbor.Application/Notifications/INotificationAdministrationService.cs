using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Notifications.Models;

namespace UptimeHarbor.Application.Notifications;

public interface INotificationAdministrationService
{
    Task<ServiceResult<NotificationSettingsDto>> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task<ServiceResult<TelegramChatDto>> CreateChatAsync(
        long telegramChatId,
        string name,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<TelegramChatDto>> UpdateChatAsync(
        Guid chatId,
        long telegramChatId,
        string name,
        CancellationToken cancellationToken = default);

    Task<ServiceResult> DeleteChatAsync(Guid chatId, CancellationToken cancellationToken = default);

    Task<ServiceResult> SendTestMessageAsync(Guid chatId, CancellationToken cancellationToken = default);

    Task<ServiceResult<NotificationBindingDto>> CreateBindingAsync(
        Guid chatId,
        MonitoringObjectKind objectKind,
        Guid objectId,
        CancellationToken cancellationToken = default);

    Task<ServiceResult> DeleteBindingAsync(Guid bindingId, CancellationToken cancellationToken = default);
}
