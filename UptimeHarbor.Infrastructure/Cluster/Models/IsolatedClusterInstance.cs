namespace UptimeHarbor.Infrastructure.Cluster.Models;

/// <summary>Ручная проверка специалистом конкретного прежнего экземпляра и его копий.</summary>
public record IsolatedClusterInstance(Guid NodeId, Guid StorageGeneration, bool Stopped,
    bool AutostartDisabled, bool PeerIngressBlocked, bool BrowserIngressBlocked, bool CopiesQuarantined,
    string EvidenceDigest);
