namespace UptimeHarbor.Domain.Cluster;

public class NodeChatRoute : ProjectionRecord
{
    private NodeChatRoute()
    {
    }

    public NodeChatRoute(Guid id, JournalPosition position, Guid nodeId, Guid chatId) : base(id, position)
    {
        StorageGuards.Id(nodeId, nameof(nodeId));
        StorageGuards.Id(chatId, nameof(chatId));
        NodeId = nodeId;
        ChatId = chatId;
    }

    public Guid NodeId { get; private set; }
    public Guid ChatId { get; private set; }
}
