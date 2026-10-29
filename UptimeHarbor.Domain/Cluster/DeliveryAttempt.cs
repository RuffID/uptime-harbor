namespace UptimeHarbor.Domain.Cluster;

public class DeliveryAttempt : ProjectionRecord
{
    private DeliveryAttempt()
    {
    }

    public DeliveryAttempt(Guid attemptId, JournalPosition position, Guid intentId, int number,
        Guid senderNodeId, Guid senderEpoch, DateTimeOffset startedAtUtc) : base(attemptId, position)
    {
        StorageGuards.Id(intentId, nameof(intentId));
        StorageGuards.Id(senderNodeId, nameof(senderNodeId));
        StorageGuards.Id(senderEpoch, nameof(senderEpoch));
        if (number is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(number));
        }

        StorageGuards.Utc(startedAtUtc);
        IntentId = intentId;
        Number = number;
        SenderNodeId = senderNodeId;
        SenderEpoch = senderEpoch;
        StartedAtUtc = startedAtUtc;
    }

    public Guid IntentId { get; private set; }
    public int Number { get; private set; }
    public Guid SenderNodeId { get; private set; }
    public Guid SenderEpoch { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }
    public AttemptOutcome Outcome { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public long? TelegramMessageId { get; private set; }
    public string? FailureKind { get; private set; }

    public void RecordResult(AttemptOutcome outcome, DateTimeOffset completedAtUtc,
        long? telegramMessageId, string? failureKind)
    {
        StorageGuards.Utc(completedAtUtc);
        if (!Enum.IsDefined(outcome) || outcome == AttemptOutcome.Unknown || completedAtUtc < StartedAtUtc
            || (outcome == AttemptOutcome.Succeeded) != telegramMessageId.HasValue)
        {
            throw new ArgumentException("Invalid delivery attempt result.");
        }

        if (Outcome == AttemptOutcome.Succeeded)
        {
            return;
        }

        if (failureKind is not null)
        {
            StorageGuards.Text(failureKind, 64, nameof(failureKind));
        }

        Outcome = outcome;
        CompletedAtUtc = completedAtUtc;
        TelegramMessageId = telegramMessageId;
        FailureKind = failureKind;
    }
}
