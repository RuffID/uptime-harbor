using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Domain.Notifications;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class NodeChatRouteConfiguration : ProjectionConfiguration<NodeChatRoute>
{
    protected override string TableName { get; } = "node_chat_routes";

    public override void Configure(EntityTypeBuilder<NodeChatRoute> builder)
    {
        base.Configure(builder);
        builder.HasIndex(entity => new { entity.NodeId, entity.ChatId }).IsUnique();
        builder.HasOne<ClusterMember>().WithMany().HasForeignKey(entity => entity.NodeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TelegramChat>().WithMany().HasForeignKey(entity => entity.ChatId).OnDelete(DeleteBehavior.Cascade);
    }
}
