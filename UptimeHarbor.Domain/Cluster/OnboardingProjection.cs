namespace UptimeHarbor.Domain.Cluster;

/// <summary>Версионная проекция одного приглашения с попыткой; не дополнительный журнал.</summary>
public class OnboardingProjection : ProjectionRecord
{
    private OnboardingProjection()
    {
    }

    public OnboardingProjection(Guid id, JournalPosition position, InvitationStatus status, string payload)
        : base(id, position)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentException("InvalidInvitationStatus");
        }

        StorageGuards.Payload(payload);
        Status = status;
        Payload = payload;
    }

    public int Version { get; private set; } = 1;
    public InvitationStatus Status { get; private set; }
    public string Payload { get; private set; } = string.Empty;

    public override string ToString()
    {
        return $"OnboardingProjection {Id}";
    }
}
