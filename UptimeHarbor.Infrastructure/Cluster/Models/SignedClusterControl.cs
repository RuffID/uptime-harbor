namespace UptimeHarbor.Infrastructure.Cluster.Models;

public record SignedClusterControl(string Payload, string Signature);
