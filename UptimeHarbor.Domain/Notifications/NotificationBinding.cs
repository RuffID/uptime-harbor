namespace UptimeHarbor.Domain.Notifications;

public class NotificationBinding
{
    private NotificationBinding()
    {
    }

    private NotificationBinding(
        Guid id,
        Guid telegramChatId,
        Guid? serverId,
        Guid? containerId)
    {
        EnsureValidId(id, nameof(id));
        EnsureValidId(telegramChatId, nameof(telegramChatId));

        if (serverId.HasValue == containerId.HasValue)
        {
            throw new ArgumentException("Привязка должна ссылаться ровно на один тип объекта.");
        }

        if (serverId.HasValue)
        {
            EnsureValidId(serverId.Value, nameof(serverId));
        }

        if (containerId.HasValue)
        {
            EnsureValidId(containerId.Value, nameof(containerId));
        }

        Id = id;
        TelegramChatId = telegramChatId;
        ServerId = serverId;
        ContainerId = containerId;
    }

    public Guid Id { get; private set; }

    public Guid TelegramChatId { get; private set; }

    public Guid? ServerId { get; private set; }

    public Guid? ContainerId { get; private set; }

    public Guid? OutageDeliveredIncidentId { get; private set; }

    public Guid? RecoveryDeliveredIncidentId { get; private set; }

    public long BindingRevision { get; private set; }

    public Guid BindingGeneration { get; private set; }

    public void AcceptBindingRevision(long revision, Guid generation)
    {
        EnsureValidId(generation, nameof(generation));
        if (revision <= BindingRevision)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        BindingRevision = revision;
        BindingGeneration = generation;
    }

    public static NotificationBinding ForServer(Guid id, Guid telegramChatId, Guid serverId)
    {
        return new NotificationBinding(id, telegramChatId, serverId, null);
    }

    public static NotificationBinding ForContainer(Guid id, Guid telegramChatId, Guid containerId)
    {
        return new NotificationBinding(id, telegramChatId, null, containerId);
    }

    public bool RequiresOutageDelivery(Guid incidentId)
    {
        EnsureValidId(incidentId, nameof(incidentId));
        return OutageDeliveredIncidentId != incidentId;
    }

    public bool RequiresRecoveryDelivery(Guid incidentId)
    {
        EnsureValidId(incidentId, nameof(incidentId));
        return OutageDeliveredIncidentId == incidentId
            && RecoveryDeliveredIncidentId != incidentId;
    }

    public void MarkOutageDelivered(Guid incidentId)
    {
        EnsureValidId(incidentId, nameof(incidentId));
        OutageDeliveredIncidentId = incidentId;
        RecoveryDeliveredIncidentId = null;
    }

    public void MarkRecoveryDelivered(Guid incidentId)
    {
        EnsureValidId(incidentId, nameof(incidentId));
        if (OutageDeliveredIncidentId != incidentId)
        {
            throw new InvalidOperationException(
                "Нельзя отметить recovery без успешной outage-доставки текущего инцидента.");
        }

        RecoveryDeliveredIncidentId = incidentId;
    }

    private static void EnsureValidId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Идентификатор не может быть пустым.", parameterName);
        }
    }
}
