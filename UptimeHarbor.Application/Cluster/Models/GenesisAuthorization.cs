namespace UptimeHarbor.Application.Cluster.Models;

public record GenesisAuthorization(int Version, Guid GenesisId, Guid InstallIdentity, Guid ClusterId, long Generation,
    Guid NodeId, Guid StorageGeneration, string PeerUri, string IssuerSpkiDigest, string IssuerCertificateDigest,
    string AccountOrImportDigest, Guid? MigrationId)
{
    public override string ToString()
    {
        return $"GenesisAuthorization {GenesisId}";
    }
}
