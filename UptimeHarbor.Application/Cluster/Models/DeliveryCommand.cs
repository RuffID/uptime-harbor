using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Внутреннее решение доставки в том же принятом журнале.</summary>
public record DeliveryCommand
{
    public Guid StorageGeneration { get; init; }
    public SenderAuthorityKey? Authority { get; init; }
    public Guid RoundId { get; init; }
    public Guid AttemptId { get; init; }
    public AttemptOutcome Outcome { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public long? TelegramMessageId { get; init; }
    public string? FailureKind { get; init; }
}
