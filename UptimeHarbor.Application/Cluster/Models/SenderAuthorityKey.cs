namespace UptimeHarbor.Application.Cluster.Models;

public record SenderAuthorityKey(Guid ClusterId, long Generation, long MembershipVersion,
    long Term, [property: System.Text.Json.Serialization.JsonPropertyName("LeaderNodeId")] Guid SenderNodeId, Guid SenderEpoch);
