using System.Globalization;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
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
        if (!options.Value.HasValidBotToken())
        {
            throw new NotificationDeliveryException(NotificationDeliveryFailure.InvalidConfiguration);
        }

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            $"./bot{botToken}/sendMessage")
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
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.BadRequest)
            {
                throw new NotificationDeliveryException(ClassifyFailure((int)response.StatusCode, null));
            }

            using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument body = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            JsonElement root = body.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("ok", out JsonElement ok)
                || ok.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                throw new NotificationDeliveryException(NotificationDeliveryFailure.InvalidResponse);
            }

            if (response.IsSuccessStatusCode && ok.GetBoolean())
            {
                return;
            }

            int status = (int)response.StatusCode;
            if (root.TryGetProperty("error_code", out JsonElement code)
                && code.ValueKind == JsonValueKind.Number
                && code.TryGetInt32(out int errorCode))
            {
                status = errorCode;
            }

            string? description = null;
            if (root.TryGetProperty("description", out JsonElement detail)
                && detail.ValueKind == JsonValueKind.String)
            {
                description = detail.GetString();
            }

            throw new NotificationDeliveryException(ClassifyFailure(status, description));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new NotificationDeliveryException(NotificationDeliveryFailure.Timeout);
        }
        catch (HttpRequestException)
        {
            throw new NotificationDeliveryException(NotificationDeliveryFailure.Network);
        }
        catch (IOException)
        {
            throw new NotificationDeliveryException(NotificationDeliveryFailure.Network);
        }
        catch (JsonException)
        {
            throw new NotificationDeliveryException(NotificationDeliveryFailure.InvalidResponse);
        }
    }

    private static NotificationDeliveryFailure ClassifyFailure(int status, string? description)
    {
        switch (status)
        {
            case 401:
            case 404:
                return NotificationDeliveryFailure.InvalidToken;
            case 400 when description?.Contains("chat not found", StringComparison.OrdinalIgnoreCase) == true:
                return NotificationDeliveryFailure.ChatNotFound;
            case 400:
                return NotificationDeliveryFailure.InvalidChat;
            case 403:
                return NotificationDeliveryFailure.Forbidden;
            case 429:
                return NotificationDeliveryFailure.RateLimited;
            case >= 500:
                return NotificationDeliveryFailure.Unavailable;
            default:
                return NotificationDeliveryFailure.Unknown;
        }
    }
}
