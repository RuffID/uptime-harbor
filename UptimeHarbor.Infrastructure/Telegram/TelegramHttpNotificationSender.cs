using System.Globalization;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Notifications;
using UptimeHarbor.Application.Notifications.Models;

namespace UptimeHarbor.Infrastructure.Telegram;

public class TelegramHttpNotificationSender(
    HttpClient httpClient,
    IOptions<TelegramOptions> options)
    : INotificationSender, IAcceptedTelegramTransport
{
    public async Task SendAsync(
        NotificationRecipient recipient,
        string message,
        CancellationToken cancellationToken = default)
    {
        await SendCoreAsync(recipient, message, false, cancellationToken);
    }

    public async Task<long> SendAcceptedAsync(NotificationRecipient recipient, string message, CancellationToken token)
    {
        return (await SendCoreAsync(recipient, message, true, token))!.Value;
    }

    private async Task<long?> SendCoreAsync(NotificationRecipient recipient, string message, bool requireMessageId,
        CancellationToken cancellationToken)
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
            UptimeHarbor.Application.Installation.RuntimeGenerationContext.Verify();
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode && response.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.TooManyRequests))
            {
                throw new NotificationDeliveryException(ClassifyFailure((int)response.StatusCode, null));
            }

            using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && response.Content.Headers.ContentLength == 0)
            {
                throw new NotificationDeliveryException(NotificationDeliveryFailure.RateLimited);
            }

            using MemoryStream bounded = new();
            byte[] buffer = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (bounded.Length + read > 65536)
                {
                    throw new NotificationDeliveryException(NotificationDeliveryFailure.InvalidResponse);
                }

                bounded.Write(buffer, 0, read);
            }

            bounded.Position = 0;
            using JsonDocument body = await JsonDocument.ParseAsync(bounded, cancellationToken: cancellationToken);
            JsonElement root = body.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("ok", out JsonElement ok)
                || ok.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                throw new NotificationDeliveryException(NotificationDeliveryFailure.InvalidResponse);
            }

            if (response.IsSuccessStatusCode && ok.GetBoolean())
            {
                if (root.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.Object
                    && result.TryGetProperty("message_id", out JsonElement id) && id.ValueKind == JsonValueKind.Number
                    && id.TryGetInt64(out long messageId) && messageId > 0)
                {
                    return messageId;
                }

                if (requireMessageId)
                {
                    throw new NotificationDeliveryException(NotificationDeliveryFailure.InvalidResponse);
                }

                return null;
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

            int? retryAfter = null;
            if (status == 429 && root.TryGetProperty("parameters", out JsonElement parameters)
                && parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("retry_after", out JsonElement retry)
                && retry.ValueKind == JsonValueKind.Number && retry.TryGetInt32(out int seconds) && seconds is >= 1 and <= 86400)
            {
                retryAfter = seconds;
            }

            throw new NotificationDeliveryException(ClassifyFailure(status, description), retryAfter);
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
