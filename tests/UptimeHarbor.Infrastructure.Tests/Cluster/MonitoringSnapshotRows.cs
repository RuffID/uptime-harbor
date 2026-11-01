using System.Collections.Immutable;
using Microsoft.AspNetCore.Identity;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Identity;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

internal static class MonitoringSnapshotRows
{
    public static RevisionImage Export(RevisionImage image, IEnumerable<object>? deliveries = null)
    {
        using var context = RevisionFixture.CreateContext();
        JournalPosition position = new(new ClusterIdentity(image.ClusterId, image.Generation), image.Index, image.Term);
        List<object> entities = [];
        foreach (AcceptedValue cell in image.Values.Values.Where(item => !item.Deleted))
        {
            object entity = RevisionDomainMapper.Materialize(cell, position);
            if (entity is SharedAccount account)
            {
                ApplicationUser user = new(account.UserId, account.Login);
                user.ApplySharedAccount(account);
                entity = user;
                if (!entities.OfType<ApplicationRole>().Any(role => role.Id == account.RoleId))
                {
                    entities.Add(new ApplicationRole(account.RoleId, SharedAccount.ADMINISTRATOR));
                }
                entities.Add(new IdentityUserRole<Guid> { UserId = account.UserId, RoleId = account.RoleId });
            }

            entities.Add(entity);
        }

        entities.AddRange(MonitoringProjection.Changes(new(image.ClusterId, image.Generation), image));
        if (deliveries is not null)
        {
            foreach (object delivery in deliveries)
            {
                if (delivery is DeliveryIntent intent)
                {
                    entities.RemoveAll(item => item is DeliveryIntent existing && existing.Id == intent.Id);
                }

                entities.Add(delivery);
            }
        }

        foreach (var node in image.Monitoring.Nodes)
        {
            entities.Add(new ClusterMember(node.Key, position, node.Value, "https://test.invalid/peer", node.Key.ToString("N"), 1, true));
        }

        return image with { ProjectionRows = entities.Select(entity => SnapshotSchema.Capture(context,
            context.Model.FindEntityType(entity.GetType())!, entity)).ToImmutableArray() };
    }
}
