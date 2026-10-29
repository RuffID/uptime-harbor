namespace UptimeHarbor.Domain.Cluster;

public class SharedIncident : ProjectionRecord
{
    private SharedIncident()
    {
    }

    public SharedIncident(Guid id, JournalPosition position, Guid targetId, Guid checkGeneration,
        IncidentKind kind, string transitionKey, DateTimeOffset openedAtUtc) : base(id, position)
    {
        StorageGuards.Id(targetId, nameof(targetId));
        StorageGuards.Id(checkGeneration, nameof(checkGeneration));
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        StorageGuards.Utc(openedAtUtc);
        TargetId = targetId;
        CheckGeneration = checkGeneration;
        Kind = kind;
        TransitionKey = StorageGuards.Text(transitionKey, 128, nameof(transitionKey));
        OpenedAtUtc = openedAtUtc;
    }

    public Guid TargetId { get; private set; }
    public Guid CheckGeneration { get; private set; }
    public IncidentKind Kind { get; private set; }
    public string TransitionKey { get; private set; } = string.Empty;
    public DateTimeOffset OpenedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public IncidentCompletion? Completion { get; private set; }

    public void Complete(IncidentCompletion completion, DateTimeOffset completedAtUtc)
    {
        StorageGuards.Utc(completedAtUtc);
        if (!Enum.IsDefined(completion) || CompletedAtUtc.HasValue || completedAtUtc < OpenedAtUtc)
        {
            throw new InvalidOperationException("Invalid incident completion.");
        }

        Completion = completion;
        CompletedAtUtc = completedAtUtc;
    }
}
