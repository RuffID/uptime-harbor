namespace UptimeHarbor.Domain.Cluster;

public class RecoveryEntitlement : ProjectionRecord
{
    private RecoveryEntitlement()
    {
    }

    public RecoveryEntitlement(Guid id, JournalPosition position, Guid incidentId, Guid chatId,
        Guid problemIntentId) : base(id, position)
    {
        StorageGuards.Id(incidentId, nameof(incidentId));
        StorageGuards.Id(chatId, nameof(chatId));
        StorageGuards.Id(problemIntentId, nameof(problemIntentId));
        IncidentId = incidentId;
        ChatId = chatId;
        ProblemIntentId = problemIntentId;
    }

    public Guid IncidentId { get; private set; }
    public Guid ChatId { get; private set; }
    public Guid ProblemIntentId { get; private set; }
    public Guid? RecoveryIntentId { get; private set; }
    public DateTimeOffset? ConsumedAtUtc { get; private set; }

    public void AssignRecovery(Guid recoveryIntentId)
    {
        StorageGuards.Id(recoveryIntentId, nameof(recoveryIntentId));
        if (RecoveryIntentId.HasValue && RecoveryIntentId != recoveryIntentId)
        {
            throw new InvalidOperationException("Recovery already has a stable delivery identity.");
        }

        RecoveryIntentId = recoveryIntentId;
    }

    public void Consume(DateTimeOffset succeededAtUtc)
    {
        StorageGuards.Utc(succeededAtUtc);
        if (!RecoveryIntentId.HasValue)
        {
            throw new InvalidOperationException("A recovery intent is required.");
        }

        ConsumedAtUtc ??= succeededAtUtc;
    }
}
