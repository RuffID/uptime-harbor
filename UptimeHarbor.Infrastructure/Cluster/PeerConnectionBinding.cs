using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Ожидаемая идентичность peer и проверка leaf, предъявленных для запроса.</summary>
public record PeerConnectionBinding(Guid NodeId, Guid StorageGeneration, Uri Address, string ClientFingerprint,
    Action<PeerCredential> ConfirmServer)
{
    public DynamicPeerTls.Snapshot? ExpectedLocalSnapshot { get; init; }
}
