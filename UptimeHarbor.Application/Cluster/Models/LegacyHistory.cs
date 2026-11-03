namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Неизменяемая ограниченная секция whitelist-истории; импортёр обязан исключить локальные секреты.</summary>
public record LegacyHistory(int Version, Guid MigrationId, Guid PartitionId, string SourceKind,
    string SourceCut, long RowCount, string PayloadDigest, byte[] CanonicalPayload)
{
    public override string ToString()
    {
        return $"LegacyHistory {MigrationId}/{PartitionId}";
    }
}
