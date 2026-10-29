namespace UptimeHarbor.Application.Cluster.Models;

public record HealthResult(string Status, string Reason, TimeSpan? Age);
