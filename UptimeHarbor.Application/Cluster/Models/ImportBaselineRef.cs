namespace UptimeHarbor.Application.Cluster.Models;

public record ImportBaselineRef(int Version, Guid MigrationId, Guid GenesisId, string SourceCut,
    string ManifestDigest, string BaselineDigest, long BaselineBytes, string SchemaVersion)
{
    public override string ToString()
    {
        return $"ImportBaselineRef {MigrationId}";
    }
}
