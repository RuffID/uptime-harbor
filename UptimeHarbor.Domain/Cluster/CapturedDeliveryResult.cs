namespace UptimeHarbor.Domain.Cluster;

// Локальный долговременный результат до принятия; общим подтверждением успеха не является.
public class CapturedDeliveryResult
{
    private CapturedDeliveryResult()
    {
    }

    public CapturedDeliveryResult(Guid attemptId, Guid originNodeId, AttemptOutcome outcome,
        DateTimeOffset completedAtUtc, long? telegramMessageId, string? failureKind)
    {
        StorageGuards.Id(attemptId, nameof(attemptId));
        StorageGuards.Id(originNodeId, nameof(originNodeId));
        StorageGuards.Utc(completedAtUtc);
        if (!Enum.IsDefined(outcome) || outcome == AttemptOutcome.Unknown
            || (outcome == AttemptOutcome.Succeeded) != telegramMessageId.HasValue)
        {
            throw new ArgumentException("Invalid captured result.");
        }

        Id = attemptId;
        OriginNodeId = originNodeId;
        Outcome = outcome;
        CompletedAtUtc = completedAtUtc;
        TelegramMessageId = telegramMessageId;
        FailureKind = failureKind;
    }

    public Guid Id { get; private set; }
    public Guid OriginNodeId { get; private set; }
    public AttemptOutcome Outcome { get; private set; }
    public DateTimeOffset CompletedAtUtc { get; private set; }
    public long? TelegramMessageId { get; private set; }
    public string? FailureKind { get; private set; }
}
