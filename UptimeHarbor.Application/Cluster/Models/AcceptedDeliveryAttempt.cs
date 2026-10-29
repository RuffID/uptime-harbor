using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Настоящая принятая попытка и её последний результат; успех монотонен.</summary>
public record AcceptedDeliveryAttempt(Guid Id, Guid IntentId, int Number, SenderAuthorityKey Authority,
    DateTimeOffset StartedAtUtc, long Index, long Term)
{
    public AttemptOutcome Outcome { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public long? TelegramMessageId { get; init; }
    public string? FailureKind { get; init; }
}
