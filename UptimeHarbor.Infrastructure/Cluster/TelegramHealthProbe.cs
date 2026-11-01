using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Notifications.Models;
using UptimeHarbor.Application.Notifications;
using UptimeHarbor.Infrastructure.Telegram;

namespace UptimeHarbor.Infrastructure.Cluster;

public class TelegramHealthProbe(IHttpClientFactory clients, IOptions<TelegramOptions> tokenOptions,
    IOptions<ClusterOptions> clusterOptions, NodeReadiness readiness, TimeProvider timeProvider)
{
    public const string CLIENT_NAME = "UptimeHarbor.TelegramHealth";
    private long? lastProbe;

    public async Task ProbeAsync(CancellationToken cancellationToken)
    {
        if (lastProbe.HasValue && timeProvider.GetElapsedTime(lastProbe.Value) < TimeSpan.FromMinutes(1))
        {
            return;
        }

        lastProbe = timeProvider.GetTimestamp();
        TelegramOptions options = tokenOptions.Value;
        if (!options.HasValidBotToken())
        {
            readiness.Report("Telegram", false, "TokenMissing", TimeSpan.FromSeconds(90));
            return;
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            HttpClient client = clients.CreateClient(CLIENT_NAME);
            using HttpResponseMessage response = await client.GetAsync($"./bot{options.BotToken}/getMe",
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                readiness.Report("Telegram", false, "TokenMissing", TimeSpan.FromSeconds(90));
                return;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Backoff не является доказательством общей неисправности отправителя.
                return;
            }

            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 65536)
            {
                readiness.Report("Telegram", false, "TransportUnavailable", TimeSpan.FromSeconds(90));
                return;
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            byte[] bytes = new byte[65537];
            int total = 0;
            while (total < bytes.Length)
            {
                int read = await stream.ReadAsync(bytes.AsMemory(total), timeout.Token);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total > 65536)
            {
                readiness.Report("Telegram", false, "TransportUnavailable", TimeSpan.FromSeconds(90));
                return;
            }

            using JsonDocument document = JsonDocument.Parse(bytes.AsMemory(0, total));
            JsonElement root = document.RootElement;
            bool valid = root.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True
                && root.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("id", out JsonElement id) && id.TryGetInt64(out long bot)
                && bot == clusterOptions.Value.ExpectedBotId;
            if (valid)
            {
                readiness.Report("Telegram", true, "Ready", TimeSpan.FromSeconds(90));
            }
            else
            {
                readiness.Report("Telegram", false, "BotMismatch", TimeSpan.FromSeconds(90));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            readiness.Report("Telegram", false, "TransportUnavailable", TimeSpan.FromSeconds(90));
        }
    }

    public void ReportDeliveryFailure(NotificationDeliveryFailure failure)
    {
        if (failure is NotificationDeliveryFailure.Forbidden or NotificationDeliveryFailure.ChatNotFound
            or NotificationDeliveryFailure.InvalidChat or NotificationDeliveryFailure.RateLimited)
        {
            return;
        }

        string reason = "TransportUnavailable";
        if (failure is NotificationDeliveryFailure.InvalidToken or NotificationDeliveryFailure.InvalidConfiguration)
        {
            reason = "TokenMissing";
        }

        readiness.Report("Telegram", false, reason, TimeSpan.FromSeconds(90));
    }
}
