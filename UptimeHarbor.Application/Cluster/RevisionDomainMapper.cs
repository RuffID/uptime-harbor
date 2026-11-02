using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Domain.Notifications;

namespace UptimeHarbor.Application.Cluster;

public static class RevisionDomainMapper
{
    public static object Materialize(AcceptedValue cell, JournalPosition acceptedPosition)
    {
        ConfigurationValue value = cell.Value;
        JournalPosition position = new(acceptedPosition.Cluster, cell.Revision, acceptedPosition.Term);
        switch (cell.Kind)
        {
            case DeletedEntityKind.Account:
                return RevisionAccounts.Materialize(cell);
            case DeletedEntityKind.Server:
                MonitoredServer server = new(cell.Id, value.Name!, value.Endpoint!, value.Description,
                    value.Enabled, ServerType.Server, value.ParentId);
                server.AcceptConfigurationRevision(cell.Revision, value.Generation);
                return server;
            case DeletedEntityKind.Container:
                MonitoredServer container = MonitoredServer.CreateApplication(cell.Id, value.ParentId, value.Endpoint!, value.Description, value.Enabled, value.Name);
                container.AcceptConfigurationRevision(cell.Revision, value.Generation);
                return container;
            case DeletedEntityKind.Chat:
                TelegramChat chat = new(cell.Id, value.TelegramChatId, value.Name!);
                chat.AcceptConfigurationRevision(cell.Revision);
                return chat;
            case DeletedEntityKind.Binding:
                NotificationBinding binding;
                if (value.Enabled)
                {
                    binding = NotificationBinding.ForServer(cell.Id, value.ChatId!.Value, value.ParentId!.Value);
                }
                else
                {
                    binding = NotificationBinding.ForContainer(cell.Id, value.ChatId!.Value, value.ParentId!.Value);
                }

                binding.AcceptBindingRevision(cell.Revision, value.Generation);
                return binding;
            case DeletedEntityKind.ObserverAssignment:
                return new ObserverAssignment(cell.Id, position, value.ParentId!.Value,
                    value.ChatId!.Value, value.Generation, cell.Revision, DateTimeOffset.UnixEpoch);
            case DeletedEntityKind.NodeChatRoute:
                return new NodeChatRoute(cell.Id, position, value.ParentId!.Value, value.ChatId!.Value);
            default:
                throw new InvalidDataException("UnsupportedConfigurationKind");
        }
    }
}
