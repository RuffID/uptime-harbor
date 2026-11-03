using System.Text.Json;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Telegram;

namespace UptimeHarbor.Infrastructure.Installation;

public class SetupBotIdentity(IOptions<ClusterOptions> cluster, IOptions<TelegramOptions> telegram,
    IHttpClientFactory clients) : ISetupBotIdentity
{
    public async Task<ServiceResult<long>> ReadAsync(CancellationToken token)
    {
        if (cluster.Value.ExpectedBotId > 0)
        {
            return ServiceResult<long>.Ok(cluster.Value.ExpectedBotId);
        }

        if (!telegram.Value.HasValidBotToken())
        {
            return Reject();
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            HttpClient client = clients.CreateClient(TelegramHealthProbe.CLIENT_NAME);
            using HttpResponseMessage response = await client.GetAsync($"./bot{telegram.Value.BotToken}/getMe",
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 65536)
            {
                return Reject();
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            byte[] bytes = new byte[65537];
            int length = 0;
            while (length < bytes.Length)
            {
                int read = await stream.ReadAsync(bytes.AsMemory(length), timeout.Token);
                if (read == 0)
                {
                    break;
                }

                length += read;
            }

            if (length > 65536)
            {
                return Reject();
            }

            using JsonDocument document = JsonDocument.Parse(bytes.AsMemory(0, length));
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ok", out JsonElement ok)
                && ok.ValueKind == JsonValueKind.True && root.TryGetProperty("result", out JsonElement result)
                && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("id", out JsonElement id)
                && id.TryGetInt64(out long botId) && botId > 0)
            {
                return ServiceResult<long>.Ok(botId);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            // URL содержит token: transport exception/payload наружу не передаётся.
        }

        return Reject();
    }

    private static ServiceResult<long> Reject()
    {
        return ServiceResult<long>.Fail(ServiceErrorType.Validation,
            "Не определён публичный BotId первого узла. Подготовьте Cluster:ExpectedBotId или действующий локальный токен будущего отправителя.");
    }
}
