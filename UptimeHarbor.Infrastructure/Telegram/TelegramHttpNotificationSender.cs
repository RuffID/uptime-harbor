using System.Globalization;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Notifications;
using UptimeHarbor.Application.Notifications.Models;

namespace UptimeHarbor.Infrastructure.Telegram;

public class TelegramHttpNotificationSender(
    HttpClient httpClient,
    IOptions<TelegramOptions> options)
    : INotificationSender
{
    public async Task SendAsync(
        NotificationRecipient recipient,
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!long.TryParse(
                recipient.Address,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long telegramChatId)
            || telegramChatId == 0)
        {
            throw new ArgumentException("Адрес получателя имеет неверный формат.", nameof(recipient));
        }

        string botToken = options.Value.BotToken;
        if (string.IsNullOrWhiteSpace(botToken))
        {
            throw new InvalidOperationException("Telegram bot token не настроен.");
        }

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            $"bot{botToken}/sendMessage")
        {
            Content = JsonContent.Create(new
            {
                chat_id = telegramChatId,
                text = message
            })
        };

        try
        {
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new NotificationDeliveryException("Сервис уведомлений отклонил сообщение.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NotificationDeliveryException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new NotificationDeliveryException("Не удалось доставить уведомление.");
        }
    }
}
