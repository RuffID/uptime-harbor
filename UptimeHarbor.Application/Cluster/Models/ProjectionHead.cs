namespace UptimeHarbor.Application.Cluster.Models;

public record ProjectionHead(Guid ClusterId, long Generation, long Index, long Term, string? Digest);
