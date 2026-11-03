namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Дополняет обязательный installation lock, сериализуя signer scopes одного процесса.</summary>
public class EnrollmentSigningLock
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
}
