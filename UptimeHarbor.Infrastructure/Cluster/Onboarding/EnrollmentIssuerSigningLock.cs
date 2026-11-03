namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Сериализует проверку authority и private-key effect; journal commit имеет свой короткий lock.</summary>
public sealed class EnrollmentIssuerSigningLock : IDisposable
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);

    public void Dispose()
    {
        Semaphore.Dispose();
    }
}
