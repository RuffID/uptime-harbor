namespace UptimeHarbor.Domain.Cluster;

public class DeliverySummary : ProjectionRecord
{
    private DeliverySummary()
    {
    }

    public DeliverySummary(Guid id, JournalPosition position, Guid planId, Guid chatId, int partNumber,
        long cutIndex, Guid intentId, string text) : base(id, position)
    {
        StorageGuards.Id(planId, nameof(planId));
        StorageGuards.Id(chatId, nameof(chatId));
        StorageGuards.Id(intentId, nameof(intentId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partNumber);
        if (cutIndex <= 0 || cutIndex > position.LogIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(cutIndex));
        }

        PlanId = planId;
        ChatId = chatId;
        PartNumber = partNumber;
        CutIndex = cutIndex;
        IntentId = intentId;
        Text = StorageGuards.Text(text, 3500, nameof(text));
    }

    public Guid PlanId { get; private set; }
    public Guid ChatId { get; private set; }
    public int PartNumber { get; private set; }
    public long CutIndex { get; private set; }
    public Guid IntentId { get; private set; }
    public string Text { get; private set; } = string.Empty;
}
