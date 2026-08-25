using System.Globalization;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Notifications.Models;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Domain.Notifications;

namespace UptimeHarbor.Application.Notifications;

public class NotificationAdministrationService(
    IUserAccessContext accessContext,
    INotificationSettingsUnitOfWork unitOfWork,
    INotificationSender sender)
    : INotificationAdministrationService
{
    public async Task<ServiceResult<NotificationSettingsDto>> GetSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        ServiceError? accessError = GetAdministratorAccessError();
        if (accessError is not null)
        {
            return ServiceResult<NotificationSettingsDto>.Fail(accessError);
        }

        List<TelegramChat> chats = await unitOfWork.TelegramChats.GetAllAsync(cancellationToken);
        List<NotificationBinding> bindings = await unitOfWork.NotificationBindings.GetAllAsync(cancellationToken);
        List<MonitoredServer> servers = await unitOfWork.MonitoredServers.GetAllAsync(cancellationToken);
        List<MonitoredContainer> containers = await unitOfWork.MonitoredContainers.GetAllAsync(cancellationToken);

        List<NotificationTargetDto> targets = servers
            .Select(server => new NotificationTargetDto(
                server.Id,
                MonitoringObjectKind.Server,
                null,
                GetDisplayName(server.LastKnownName, server.Id)))
            .Concat(containers.Select(container => new NotificationTargetDto(
                container.Id,
                MonitoringObjectKind.Container,
                container.ServerId,
                GetDisplayName(container.LastKnownName, container.Id))))
            .ToList();

        return ServiceResult<NotificationSettingsDto>.Ok(new NotificationSettingsDto(
            chats.Select(MapChat).ToList(),
            bindings.Select(MapBinding).ToList(),
            targets));
    }

    public async Task<ServiceResult<TelegramChatDto>> CreateChatAsync(
        long telegramChatId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ServiceError? error = GetAdministratorAccessError() ?? ValidateChat(telegramChatId, name);
        if (error is not null)
        {
            return ServiceResult<TelegramChatDto>.Fail(error);
        }

        TelegramChat? duplicate = await unitOfWork.TelegramChats.GetByTelegramChatIdAsync(
            telegramChatId,
            cancellationToken);
        if (duplicate is not null)
        {
            return ServiceResult<TelegramChatDto>.Fail(
                ServiceErrorType.Conflict,
                "Telegram chat ID уже добавлен.",
                "telegramChatId");
        }

        TelegramChat chat = new(Guid.NewGuid(), telegramChatId, name);
        unitOfWork.TelegramChats.Create(chat);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ServiceResult<TelegramChatDto>.Ok(MapChat(chat));
    }

    public async Task<ServiceResult<TelegramChatDto>> UpdateChatAsync(
        Guid chatId,
        long telegramChatId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ServiceError? error = GetAdministratorAccessError() ?? ValidateChat(telegramChatId, name);
        if (error is not null)
        {
            return ServiceResult<TelegramChatDto>.Fail(error);
        }

        TelegramChat? chat = await unitOfWork.TelegramChats.GetItemByIdAsync(chatId, cancellationToken);
        if (chat is null)
        {
            return ServiceResult<TelegramChatDto>.Fail(ServiceErrorType.NotFound, "Telegram-чат не найден.");
        }

        TelegramChat? duplicate = await unitOfWork.TelegramChats.GetByTelegramChatIdAsync(
            telegramChatId,
            cancellationToken);
        if (duplicate is not null && duplicate.Id != chatId)
        {
            return ServiceResult<TelegramChatDto>.Fail(
                ServiceErrorType.Conflict,
                "Telegram chat ID уже добавлен.",
                "telegramChatId");
        }

        chat.Update(telegramChatId, name);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ServiceResult<TelegramChatDto>.Ok(MapChat(chat));
    }

    public async Task<ServiceResult> DeleteChatAsync(
        Guid chatId,
        CancellationToken cancellationToken = default)
    {
        ServiceError? accessError = GetAdministratorAccessError();
        if (accessError is not null)
        {
            return ServiceResult.Fail(accessError);
        }

        TelegramChat? chat = await unitOfWork.TelegramChats.GetItemByIdAsync(chatId, cancellationToken);
        if (chat is null)
        {
            return ServiceResult.Fail(ServiceErrorType.NotFound, "Telegram-чат не найден.");
        }

        unitOfWork.TelegramChats.Delete(chat);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> SendTestMessageAsync(
        Guid chatId,
        CancellationToken cancellationToken = default)
    {
        ServiceError? accessError = GetAdministratorAccessError();
        if (accessError is not null)
        {
            return ServiceResult.Fail(accessError);
        }

        TelegramChat? chat = await unitOfWork.TelegramChats.GetItemByIdAsync(chatId, cancellationToken);
        if (chat is null)
        {
            return ServiceResult.Fail(ServiceErrorType.NotFound, "Telegram-чат не найден.");
        }

        try
        {
            await sender.SendAsync(
                new NotificationRecipient(
                    chat.Id,
                    chat.TelegramChatId.ToString(CultureInfo.InvariantCulture)),
                "UptimeHarbor: тестовое уведомление.",
                cancellationToken);
        }
        catch (NotificationDeliveryException)
        {
            return ServiceResult.Fail(
                ServiceErrorType.Conflict,
                "Telegram не принял тестовое сообщение.");
        }

        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<NotificationBindingDto>> CreateBindingAsync(
        Guid chatId,
        MonitoringObjectKind objectKind,
        Guid objectId,
        CancellationToken cancellationToken = default)
    {
        ServiceError? accessError = GetAdministratorAccessError();
        if (accessError is not null)
        {
            return ServiceResult<NotificationBindingDto>.Fail(accessError);
        }

        if (chatId == Guid.Empty || objectId == Guid.Empty)
        {
            return ServiceResult<NotificationBindingDto>.Fail(
                ServiceErrorType.Validation,
                "Чат и объект обязательны.");
        }

        if (objectKind is not MonitoringObjectKind.Server and not MonitoringObjectKind.Container)
        {
            return ServiceResult<NotificationBindingDto>.Fail(
                ServiceErrorType.Validation,
                "Неизвестный тип объекта.",
                "objectKind");
        }

        TelegramChat? chat = await unitOfWork.TelegramChats.GetItemByIdAsync(chatId, cancellationToken);
        if (chat is null)
        {
            return ServiceResult<NotificationBindingDto>.Fail(
                ServiceErrorType.Validation,
                "Telegram-чат не найден.",
                "chatId");
        }

        List<NotificationBinding> existingBindings = objectKind switch
        {
            MonitoringObjectKind.Server => await unitOfWork.NotificationBindings.GetByServerIdAsync(
                objectId,
                cancellationToken),
            MonitoringObjectKind.Container => await unitOfWork.NotificationBindings.GetByContainerIdAsync(
                objectId,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(objectKind), objectKind, "Неизвестный тип объекта.")
        };
        if (existingBindings.Any(binding => binding.TelegramChatId == chatId))
        {
            return ServiceResult<NotificationBindingDto>.Fail(
                ServiceErrorType.Conflict,
                "Такая привязка уже существует.");
        }

        NotificationBinding binding;
        if (objectKind == MonitoringObjectKind.Server)
        {
            MonitoredServer? server = await unitOfWork.MonitoredServers.GetItemByIdAsync(objectId, cancellationToken);
            if (server is null)
            {
                return ServiceResult<NotificationBindingDto>.Fail(
                    ServiceErrorType.Validation,
                    "Сервер не найден.",
                    "objectId");
            }

            binding = NotificationBinding.ForServer(Guid.NewGuid(), chatId, objectId);
        }
        else
        {
            MonitoredContainer? container = await unitOfWork.MonitoredContainers.GetItemByIdAsync(
                objectId,
                cancellationToken);
            if (container is null)
            {
                return ServiceResult<NotificationBindingDto>.Fail(
                    ServiceErrorType.Validation,
                    "Контейнер не найден.",
                    "objectId");
            }

            binding = NotificationBinding.ForContainer(Guid.NewGuid(), chatId, objectId);
        }

        unitOfWork.NotificationBindings.Create(binding);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ServiceResult<NotificationBindingDto>.Ok(MapBinding(binding));
    }

    public async Task<ServiceResult> DeleteBindingAsync(
        Guid bindingId,
        CancellationToken cancellationToken = default)
    {
        ServiceError? accessError = GetAdministratorAccessError();
        if (accessError is not null)
        {
            return ServiceResult.Fail(accessError);
        }

        NotificationBinding? binding = await unitOfWork.NotificationBindings.GetItemByIdAsync(
            bindingId,
            cancellationToken);
        if (binding is null)
        {
            return ServiceResult.Fail(ServiceErrorType.NotFound, "Привязка не найдена.");
        }

        unitOfWork.NotificationBindings.Delete(binding);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    private ServiceError? GetAdministratorAccessError()
    {
        if (!accessContext.IsAuthenticated)
        {
            return new ServiceError(ServiceErrorType.Unauthorized, "Требуется аутентификация.");
        }

        if (!accessContext.IsEnabled || !accessContext.IsAdministrator)
        {
            return new ServiceError(ServiceErrorType.Forbidden, "Операция доступна только администратору.");
        }

        return null;
    }

    private static ServiceError? ValidateChat(long telegramChatId, string name)
    {
        if (telegramChatId == 0)
        {
            return new ServiceError(
                ServiceErrorType.Validation,
                "Telegram chat ID не может быть равен нулю.",
                "telegramChatId");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return new ServiceError(ServiceErrorType.Validation, "Название обязательно.", "name");
        }

        if (name.Trim().Length > ModelConstraints.TELEGRAM_CHAT_NAME_MAX_LENGTH)
        {
            return new ServiceError(
                ServiceErrorType.Validation,
                $"Название не должно превышать {ModelConstraints.TELEGRAM_CHAT_NAME_MAX_LENGTH} символов.",
                "name");
        }

        return null;
    }

    private static string GetDisplayName(string? name, Guid id) =>
        name ?? $"Имя не получено · {id.ToString("N")[..8]}";

    private static TelegramChatDto MapChat(TelegramChat chat) =>
        new(chat.Id, chat.TelegramChatId, chat.Name);

    private static NotificationBindingDto MapBinding(NotificationBinding binding) =>
        new(binding.Id, binding.TelegramChatId, binding.ServerId, binding.ContainerId);
}
