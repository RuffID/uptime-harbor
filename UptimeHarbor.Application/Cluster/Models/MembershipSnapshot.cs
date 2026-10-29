namespace UptimeHarbor.Application.Cluster.Models;

public record MembershipSnapshot(long Version, IReadOnlyDictionary<Guid, Uri> Voters);
