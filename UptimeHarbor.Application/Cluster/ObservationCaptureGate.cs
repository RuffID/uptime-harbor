namespace UptimeHarbor.Application.Cluster;

/// <summary>Один экземпляр NodeId сериализует резервирование local sequence между scopes.</summary>
public class ObservationCaptureGate
{
    public SemaphoreSlim Serial { get; } = new(1, 1);
}
