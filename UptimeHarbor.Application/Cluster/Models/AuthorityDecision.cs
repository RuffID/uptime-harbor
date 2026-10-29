namespace UptimeHarbor.Application.Cluster.Models;

public record AuthorityDecision(SenderAuthorityKey Key, Guid RoundId, long AppliedIndex);
