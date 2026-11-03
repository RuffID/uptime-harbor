namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Принятое поручение администратора, повторно сверяемое с текущим аккаунтом.</summary>
public record OnboardingDelegation(Guid ActorId, long ActorRevision, long Revision, DateTimeOffset ReauthenticatedAtUtc);
