using System.Globalization;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

/// <summary>Закрытые категории результатов и ограниченный retry, включая Telegram retry_after.</summary>
public static class DeliveryRetry
{
    public static void ValidateFailure(string? failure)
    {
        if (failure is null)
        {
            return;
        }

        string category = failure.Split('/')[0];
        if (category is not ("InvalidConfiguration" or "InvalidToken" or "InvalidChat" or "ChatNotFound"
            or "Forbidden" or "RateLimited" or "Unavailable" or "Timeout" or "Network" or "InvalidResponse"
            or "Unknown" or "Ambiguous" or "CancelledBeforeTransport"))
        {
            throw new InvalidDataException("InvalidDeliveryFailure");
        }

        if (failure != category && (category != "RateLimited" || !int.TryParse(failure.AsSpan(category.Length + 1),
            NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds is < 1 or > 86400))
        {
            throw new InvalidDataException("InvalidRetryAfter");
        }
    }

    public static DateTimeOffset? Next(int number, AttemptOutcome outcome, string? failure, DateTimeOffset atUtc)
    {
        ValidateFailure(failure);
        if (number >= 5 || outcome == AttemptOutcome.Succeeded)
        {
            return null;
        }

        if (failure is "InvalidConfiguration" or "InvalidToken" or "InvalidChat" or "ChatNotFound" or "Forbidden")
        {
            return null;
        }

        int[] backoff = [5, 30, 120, 300];
        int delay = backoff[number - 1];
        if (failure?.StartsWith("RateLimited/", StringComparison.Ordinal) == true)
        {
            delay = Math.Max(delay, int.Parse(failure.AsSpan(12), CultureInfo.InvariantCulture));
        }

        return atUtc.AddSeconds(delay);
    }
}
