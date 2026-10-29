namespace UptimeHarbor.Infrastructure.Cluster;

public record ClusterDiskManifest(Guid ClusterId, long Generation, Guid NodeId, Guid StorageGeneration,
    string PublicUri, Guid BootstrapId);
