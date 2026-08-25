using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Monitoring.Models;
using UptimeHarbor.Domain;

namespace UptimeHarbor.Infrastructure.Monitoring;

public class HttpMonitoringEndpointChecker(
    IHttpClientFactory httpClientFactory,
    IOptions<MonitoringOptions> options,
    TimeProvider timeProvider)
    : IServerEndpointChecker, IContainerEndpointChecker
{
    public const string HTTP_CLIENT_NAME = "MonitoringEndpoints";

    public async Task<ServerEndpointCheckResult> CheckAsync(
        string endpointUrl,
        CancellationToken cancellationToken = default)
    {
        HttpPayloadResult payload = await GetPayloadAsync(endpointUrl, cancellationToken);
        if (payload.FailureKind is EndpointCheckFailureKind failureKind)
        {
            return ServerEndpointCheckResult.Failed(failureKind, payload.CheckedAtUtc);
        }

        using JsonDocument document = payload.Document
            ?? throw new InvalidOperationException("Успешный HTTP-ответ должен содержать JSON-документ.");
        if (!TryReadName(document.RootElement, out string name))
        {
            return ServerEndpointCheckResult.Failed(
                EndpointCheckFailureKind.InvalidName,
                payload.CheckedAtUtc);
        }

        return ServerEndpointCheckResult.Succeeded(name, payload.CheckedAtUtc);
    }

    async Task<ContainerEndpointCheckResult> IContainerEndpointChecker.CheckAsync(
        string endpointUrl,
        CancellationToken cancellationToken)
    {
        HttpPayloadResult payload = await GetPayloadAsync(endpointUrl, cancellationToken);
        if (payload.FailureKind is EndpointCheckFailureKind failureKind)
        {
            return ContainerEndpointCheckResult.Failed(failureKind, payload.CheckedAtUtc);
        }

        using JsonDocument document = payload.Document
            ?? throw new InvalidOperationException("Успешный HTTP-ответ должен содержать JSON-документ.");
        if (!TryReadName(document.RootElement, out string name))
        {
            return ContainerEndpointCheckResult.Failed(
                EndpointCheckFailureKind.InvalidName,
                payload.CheckedAtUtc);
        }

        if (!TryReadStartedAtUtc(
            document.RootElement,
            payload.CheckedAtUtc,
            out DateTimeOffset startedAtUtc))
        {
            return ContainerEndpointCheckResult.Failed(
                EndpointCheckFailureKind.InvalidStartedAtUtc,
                payload.CheckedAtUtc);
        }

        return ContainerEndpointCheckResult.Succeeded(
            name,
            startedAtUtc,
            payload.CheckedAtUtc);
    }

    private async Task<HttpPayloadResult> GetPayloadAsync(
        string endpointUrl,
        CancellationToken cancellationToken)
    {
        TimeSpan timeout = TimeSpan.FromSeconds(options.Value.TimeoutSeconds);
        using CancellationTokenSource timeoutSource = new(timeout, timeProvider);
        using CancellationTokenSource requestSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            HttpClient client = httpClientFactory.CreateClient(HTTP_CLIENT_NAME);
            using HttpRequestMessage request = new(HttpMethod.Get, endpointUrl);
            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestSource.Token);
            DateTimeOffset checkedAtUtc = timeProvider.GetUtcNow();
            if (!response.IsSuccessStatusCode)
            {
                return HttpPayloadResult.Failed(
                    EndpointCheckFailureKind.HttpStatus,
                    checkedAtUtc);
            }

            try
            {
                await using Stream content = await response.Content.ReadAsStreamAsync(requestSource.Token);
                JsonDocument document = await JsonDocument.ParseAsync(
                    content,
                    cancellationToken: requestSource.Token);
                return HttpPayloadResult.Succeeded(document, timeProvider.GetUtcNow());
            }
            catch (JsonException)
            {
                return HttpPayloadResult.Failed(
                    EndpointCheckFailureKind.InvalidJson,
                    timeProvider.GetUtcNow());
            }
        }
        catch (OperationCanceledException) when (
            timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return HttpPayloadResult.Failed(
                EndpointCheckFailureKind.Timeout,
                timeProvider.GetUtcNow());
        }
        catch (HttpRequestException)
        {
            return HttpPayloadResult.Failed(
                EndpointCheckFailureKind.Network,
                timeProvider.GetUtcNow());
        }
    }

    private static bool TryReadName(JsonElement root, out string name)
    {
        name = string.Empty;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("name", out JsonElement nameElement)
            || nameElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? value = nameElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > ModelConstraints.OBJECT_NAME_MAX_LENGTH)
        {
            return false;
        }

        name = value;
        return true;
    }

    private static bool TryReadStartedAtUtc(
        JsonElement root,
        DateTimeOffset checkedAtUtc,
        out DateTimeOffset startedAtUtc)
    {
        startedAtUtc = default;
        if (!root.TryGetProperty("startedAtUtc", out JsonElement startedAtElement)
            || startedAtElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? value = startedAtElement.GetString();
        bool hasExplicitUtcOffset = value?.EndsWith("Z", StringComparison.OrdinalIgnoreCase) == true
            || value?.EndsWith("+00:00", StringComparison.Ordinal) == true;
        return hasExplicitUtcOffset
            && DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out startedAtUtc)
            && startedAtUtc.Offset == TimeSpan.Zero
            && startedAtUtc <= checkedAtUtc;
    }

    private class HttpPayloadResult
    {
        private HttpPayloadResult(
            JsonDocument? document,
            EndpointCheckFailureKind? failureKind,
            DateTimeOffset checkedAtUtc)
        {
            Document = document;
            FailureKind = failureKind;
            CheckedAtUtc = checkedAtUtc;
        }

        public JsonDocument? Document { get; }

        public EndpointCheckFailureKind? FailureKind { get; }

        public DateTimeOffset CheckedAtUtc { get; }

        public static HttpPayloadResult Succeeded(
            JsonDocument document,
            DateTimeOffset checkedAtUtc)
        {
            return new HttpPayloadResult(document, null, checkedAtUtc);
        }

        public static HttpPayloadResult Failed(
            EndpointCheckFailureKind failureKind,
            DateTimeOffset checkedAtUtc)
        {
            return new HttpPayloadResult(null, failureKind, checkedAtUtc);
        }
    }
}
