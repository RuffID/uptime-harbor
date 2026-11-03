namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Общий лимит 30 запросов/минуту; 5 отказов/минуту блокируют IP. Источник задаёт transport.</summary>
public class EnrollmentRateLimiter(TimeProvider time)
{
    private readonly object sync = new();
    private readonly Dictionary<string, (long Started, int Count)> origins = new(StringComparer.Ordinal);
    private long endpointStarted;
    private int endpointCount;

    public bool TryAcquire(string trustedOrigin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedOrigin);
        lock (sync)
        {
            long now = time.GetTimestamp();
            if (time.GetElapsedTime(endpointStarted, now) >= TimeSpan.FromMinutes(1))
            {
                endpointStarted = now;
                endpointCount = 0;
                origins.Clear();
            }

            (long started, int count) = origins.GetValueOrDefault(trustedOrigin, (now, 0));
            if (endpointCount >= 30 || count >= 5)
            {
                return false;
            }

            endpointCount++;
            return true;
        }
    }

    public void Rejected(string trustedOrigin)
    {
        lock (sync)
        {
            (long started, int count) = origins.GetValueOrDefault(trustedOrigin, (time.GetTimestamp(), 0));
            origins[trustedOrigin] = (started, count + 1);
        }
    }
}
