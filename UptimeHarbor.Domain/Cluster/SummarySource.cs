namespace UptimeHarbor.Domain.Cluster;

public class SummarySource : ProjectionRecord
{
    private SummarySource()
    {
    }

    public SummarySource(Guid id, JournalPosition position, Guid summaryId, Guid sourceEventId,
        Guid sourceIntentId) : base(id, position)
    {
        StorageGuards.Id(summaryId, nameof(summaryId));
        StorageGuards.Id(sourceEventId, nameof(sourceEventId));
        StorageGuards.Id(sourceIntentId, nameof(sourceIntentId));
        SummaryId = summaryId;
        SourceEventId = sourceEventId;
        SourceIntentId = sourceIntentId;
    }

    public Guid SummaryId { get; private set; }
    public Guid SourceEventId { get; private set; }
    public Guid SourceIntentId { get; private set; }
}
