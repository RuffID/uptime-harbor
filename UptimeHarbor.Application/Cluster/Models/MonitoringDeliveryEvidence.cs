namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Ссылка на принятые intent и успешную attempt. Producer результатов принадлежит этапу 07.</summary>
public record MonitoringDeliveryEvidence(Guid IntentId, Guid AttemptId, long AcceptedIndex, long AcceptedTerm,
    int AttemptNumber, Guid SenderNodeId, Guid SenderEpoch, DateTimeOffset StartedAtUtc,
    DateTimeOffset SucceededAtUtc, long TelegramMessageId);
