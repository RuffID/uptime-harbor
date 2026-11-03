namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Ограниченный process-local replay cache только для read-only TLS probes.</summary>
public class EnrollmentProbeReplay(TimeProvider time)
{
    private readonly Dictionary<Guid, long> used = [];

    public bool Accept(Guid id)
    {
        lock (used)
        {
            foreach (Guid expired in used.Where(item => time.GetElapsedTime(item.Value) >= TimeSpan.FromMinutes(2))
                .Select(item => item.Key).ToArray())
            {
                used.Remove(expired);
            }

            return used.Count < 300 && used.TryAdd(id, time.GetTimestamp());
        }
    }
}
