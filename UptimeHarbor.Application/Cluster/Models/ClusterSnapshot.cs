namespace UptimeHarbor.Application.Cluster.Models;

public record ClusterSnapshot(long Index, long Term, long Length, Func<Stream> OpenRead);
