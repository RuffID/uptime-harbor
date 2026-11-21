namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

/// <summary>Минимальное состояние принятого запроса для общего администратора.</summary>
public record VoterContinuationAdministratorStatus(Guid RequestId, Guid NodeId, string RequestDigest, Guid? ConfirmationId);
