using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Security;

namespace UptimeHarbor.Application.Cluster;

/// <summary>Публикация immutable состояния только после SQL commit/replay; отказ закрывает чтение прав.</summary>
public class RevisionStateStore : IAcceptedAccountReader
{
    private RevisionImage? image;
    private volatile bool available;
    private volatile bool recovering;

    public RevisionImage? Current => Volatile.Read(ref image);
    public bool Available => available;

    public AcceptedAccount? Find(Guid userId)
    {
        RevisionImage? current = Current;
        if (!available || current is null)
        {
            return null;
        }

        return RevisionAccounts.Find(current, userId);
    }

    public void Publish(RevisionImage accepted)
    {
        Volatile.Write(ref image, accepted);
        available = !recovering;
    }

    public void BeginRecovery()
    {
        recovering = true;
        available = false;
    }

    public void ConfirmRecovery(RevisionImage accepted)
    {
        Volatile.Write(ref image, accepted);
        recovering = false;
        available = true;
    }

    public void Invalidate()
    {
        available = false;
    }
}
