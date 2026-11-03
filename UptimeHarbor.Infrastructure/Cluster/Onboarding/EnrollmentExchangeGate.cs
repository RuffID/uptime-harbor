namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Сериализация локального command intent, не SQL/consensus блокировка.</summary>
public class EnrollmentExchangeGate
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
}
