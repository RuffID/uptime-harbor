using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Security;

namespace UptimeHarbor.Application.Cluster;

/// <summary>Публикация immutable состояния только после SQL commit/replay; отказ закрывает чтение прав.</summary>
public class RevisionStateStore : IAcceptedAccountReader
{
    private readonly object sync = new();
    private RevisionImage? image;
    private volatile bool available;
    private volatile bool recovering;
    private long recoveryVersion;

    public RevisionImage? Current => Volatile.Read(ref image);
    public bool Available => available;
    public long RecoveryVersion => Interlocked.Read(ref recoveryVersion);
    public event Action? Changed;

    public AcceptedAccount? Find(Guid userId)
    {
        lock (sync)
        {
            if (!available || image is null)
            {
                return null;
            }

            return RevisionAccounts.Find(image, userId);
        }
    }

    public void Publish(RevisionImage accepted)
    {
        lock (sync)
        {
            Volatile.Write(ref image, accepted);
            available = !recovering;
        }

        Changed?.Invoke();
    }

    public void BeginRecovery()
    {
        lock (sync)
        {
            Interlocked.Increment(ref recoveryVersion);
            recovering = true;
            available = false;
        }

        Changed?.Invoke();
    }

    public bool ConfirmRecovery(RevisionImage accepted, long expectedRecoveryVersion)
    {
        lock (sync)
        {
            if (expectedRecoveryVersion != recoveryVersion)
            {
                return false;
            }

            Volatile.Write(ref image, accepted);
            recovering = false;
            available = true;
        }

        Changed?.Invoke();
        return true;
    }

    public void Invalidate()
    {
        lock (sync)
        {
            Interlocked.Increment(ref recoveryVersion);
            recovering = true;
            available = false;
        }

        Changed?.Invoke();
    }
}
