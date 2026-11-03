namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Безопасный immutable ключ browser-команды для retry после смены лидера.</summary>
public record OnboardingRequestReceipt(Guid OperationId, Guid InvitationId, Guid AttemptId,
    Guid ActorId, string Action, Guid? ReplacesInvitationId, string Digest);
