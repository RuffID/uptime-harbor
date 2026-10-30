namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Неизменяемое происхождение настоящей попытки из выбранной копии.</summary>
public record RecoveredAttemptIdentity(Guid IntentId, int Number, SenderAuthorityKey Authority, DateTimeOffset StartedAtUtc);
