namespace UptimeHarbor.Infrastructure.Cluster.Models;

/// <summary>Подписывается ответственным специалистом после фактической внешней изоляции, не по timeout.</summary>
public record ManualIsolationConfirmation(Guid ProcedureId, Guid OperatorId, DateTimeOffset VerifiedAtUtc,
    IReadOnlyList<IsolatedClusterInstance> Instances, long BotId, bool PreviousBotTokenRevoked,
    bool PreviousPeerTrustRevoked, string ExternalRecordDigest);
