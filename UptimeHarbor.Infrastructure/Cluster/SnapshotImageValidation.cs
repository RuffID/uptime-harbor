using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Text.Json;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Domain.Notifications;
using UptimeHarbor.Infrastructure.Identity;
using UptimeHarbor.Infrastructure.Persistence;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Проверяет соответствие конфигурационной проекции принятому образу до начала SQL install.</summary>
public static class SnapshotImageValidation
{
    public static void Validate(UptimeHarborDbContext context, RevisionImage image)
    {
        SnapshotSchema.Validate(context, image.ProjectionRows);
        JournalPosition position = new(new ClusterIdentity(image.ClusterId, image.Generation), image.Index, image.Term);
        Dictionary<Type, HashSet<Guid>> expected = [];
        foreach (AcceptedValue cell in image.Values.Values)
        {
            if (cell.Revision <= 0 || cell.Revision > image.Index || image.Values[cell.Id] != cell)
            {
                throw new InvalidDataException("SnapshotValueRevisionMismatch");
            }

            if (cell.Deleted)
            {
                Compare(context, image, new Tombstone(cell.Id, new JournalPosition(position.Cluster, cell.Revision, image.Term),
                    cell.Kind, cell.DeletedAtUtc!.Value), ["Id", "LogIndex", "Kind", "DeletedAtUtc"]);
                if (cell.Kind != DeletedEntityKind.Account)
                {
                    continue;
                }
            }

            object entity = RevisionDomainMapper.Materialize(cell, position);
            if (entity is SharedAccount account)
            {
                ApplicationUser user = new(account.UserId, account.Login);
                user.ApplySharedAccount(account);
                entity = user;
                string membershipType = context.Model.FindEntityType(typeof(IdentityUserRole<Guid>))!.Name;
                SharedSnapshotRow membership = image.ProjectionRows.Single(row => row.Entity == membershipType
                    && row.Fields["UserId"].GetGuid() == account.UserId);
                if (membership.Fields["RoleId"].GetGuid() != account.RoleId)
                {
                    throw new InvalidDataException("SnapshotRoleMismatch");
                }

                Compare(context, image, new ApplicationRole(account.RoleId, SharedAccount.ADMINISTRATOR), ["Id", "Name", "NormalizedName"]);
            }

            Type type = entity.GetType();
            if (!expected.TryGetValue(type, out HashSet<Guid>? ids))
            {
                ids = [];
                expected.Add(type, ids);
            }

            ids.Add(cell.Id);
            Compare(context, image, entity, ConfigurationFields(type));
        }

        Type[] configurationTypes = [typeof(ApplicationUser), typeof(MonitoredServer), typeof(MonitoredContainer),
            typeof(TelegramChat), typeof(NotificationBinding), typeof(ObserverAssignment), typeof(NodeChatRoute)];
        foreach (Type type in configurationTypes)
        {
            HashSet<Guid> ids = expected.GetValueOrDefault(type) ?? [];
            Guid[] actual = image.ProjectionRows.Where(row => row.Entity == type.FullName)
                .Select(row => row.Fields["Id"].GetGuid()).ToArray();
            if (!ids.SetEquals(actual))
            {
                throw new InvalidDataException("SnapshotConfigurationSetMismatch");
            }
        }
    }

    private static string[] ConfigurationFields(Type type)
    {
        if (type == typeof(ApplicationUser))
        {
            return ["Id", "UserName", "NormalizedUserName", "PasswordHash", "PasswordHashFormatVersion",
                "SecurityStamp", "ConcurrencyStamp", "IsEnabled", "UserRevision", "DeletedAtUtc"];
        }

        if (type == typeof(MonitoredServer))
        {
            return ["Id", "Name", "EndpointUrl", "Description", "IsMonitoringEnabled", "ConfigRevision", "CheckGeneration"];
        }

        if (type == typeof(MonitoredContainer))
        {
            return ["Id", "ServerId", "EndpointUrl", "Description", "IsMonitoringEnabled", "ConfigRevision", "CheckGeneration"];
        }

        if (type == typeof(TelegramChat))
        {
            return ["Id", "Name", "TelegramChatId", "ConfigRevision"];
        }

        if (type == typeof(NotificationBinding))
        {
            return ["Id", "TelegramChatId", "ServerId", "ContainerId", "BindingRevision", "BindingGeneration"];
        }

        if (type == typeof(ObserverAssignment))
        {
            return ["Id", "ClusterId", "ClusterGeneration", "LogIndex", "TargetId", "ObserverNodeId",
                "CheckGeneration", "ConfigRevision", "AssignedAtUtc"];
        }

        if (type == typeof(NodeChatRoute))
        {
            return ["Id", "ClusterId", "ClusterGeneration", "LogIndex", "NodeId", "ChatId"];
        }

        return ["Id", "ClusterId", "ClusterGeneration", "LogIndex"];
    }

    private static void Compare(UptimeHarborDbContext context, RevisionImage image, object entity, string[] fields)
    {
        IEntityType type = context.Model.FindEntityType(entity.GetType())!;
        SharedSnapshotRow expected = SnapshotSchema.Capture(context, type, entity);
        SharedSnapshotRow actual = image.ProjectionRows.Single(row => row.Entity == type.Name
            && row.Fields["Id"].GetGuid() == expected.Fields["Id"].GetGuid());
        foreach (string field in fields)
        {
            if (!JsonElement.DeepEquals(expected.Fields[field], actual.Fields[field]))
            {
                throw new InvalidDataException("SnapshotConfigurationMismatch");
            }
        }
    }
}
