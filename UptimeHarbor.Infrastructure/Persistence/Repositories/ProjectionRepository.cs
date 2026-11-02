using EFCoreLibrary.Abstractions.Database.Repository.Base;
using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Identity;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Domain.Notifications;

namespace UptimeHarbor.Infrastructure.Persistence.Repositories;

public class ProjectionRepository(
    UptimeHarborDbContext context,
    IContextGetItemByPredicateRepository<ProjectionCursor, UptimeHarborDbContext> cursorReader,
    IContextGetItemByPredicateRepository<AppliedJournalReceipt, UptimeHarborDbContext> receiptReader,
    IContextGetItemByPredicateRepository<OperationDecision, UptimeHarborDbContext> decisionReader)
    : IProjectionRepository
{
    public Task<ProjectionCursor?> GetCursorAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        return cursorReader.GetItemByPredicateAsync(item => item.Id == nodeId,
            asNoTracking: false, ct: cancellationToken);
    }

    public Task<AppliedJournalReceipt?> GetReceiptAsync(JournalPosition position, CancellationToken cancellationToken = default)
    {
        return receiptReader.GetItemByPredicateAsync(item => item.ClusterId == position.Cluster.ClusterId
            && item.ClusterGeneration == position.Cluster.Generation && item.LogIndex == position.LogIndex,
            asNoTracking: true, ct: cancellationToken);
    }

    public Task<OperationDecision?> GetDecisionAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        return decisionReader.GetItemByPredicateAsync(item => item.Id == operationId,
            asNoTracking: true, ct: cancellationToken);
    }

    public void AddCursor(ProjectionCursor cursor)
    {
        context.Add(cursor);
    }

    public void AddReceipt(AppliedJournalReceipt receipt)
    {
        context.Add(receipt);
    }

    public async Task StageAsync(ProjectionWriteSet changes, CancellationToken cancellationToken = default)
    {
        foreach (ProjectionRecord record in changes.Records)
        {
            record.GetPosition().EnsureSameCluster(changes.Receipt.GetPosition().Cluster);
            if (record.LogIndex > changes.Receipt.LogIndex || record is AppliedJournalReceipt)
            {
                throw new InvalidOperationException("Invalid projection provenance.");
            }

            if (record is OperationDecision decision)
            {
                OperationDecision? existing = await context.Set<OperationDecision>().FindAsync([decision.Id], cancellationToken);
                if (existing is not null)
                {
                    existing.EnsureSameRequest(decision.Digest);
                    if (existing.Outcome != decision.Outcome || existing.ReplacedBy != decision.ReplacedBy
                        || existing.ConflictId != decision.ConflictId)
                    {
                        throw new InvalidOperationException("A terminal operation decision cannot be rewritten.");
                    }

                    continue;
                }
            }

            if (record is ClusterMember or ObserverAssignment or NodeChatRoute)
            {
                await EnsureNotDeletedAsync(record.Id, cancellationToken);
            }

            if (record is ObserverAssignment assignment)
            {
                await EnsureNotDeletedAsync(assignment.TargetId, cancellationToken);
                await EnsureNotDeletedAsync(assignment.ObserverNodeId, cancellationToken);
            }
            else if (record is NodeChatRoute route)
            {
                await EnsureNotDeletedAsync(route.NodeId, cancellationToken);
                await EnsureNotDeletedAsync(route.ChatId, cancellationToken);
            }

            await StageEntityAsync(record, record.Id, cancellationToken);
        }

        if (changes.Account is not null)
        {
            await StageAccountAsync(changes.Account, cancellationToken);
        }

        foreach (SharedAccount account in changes.Accounts)
        {
            await StageAccountAsync(account, cancellationToken);
        }

        foreach (var server in changes.Servers)
        {
            await EnsureNotDeletedAsync(server.Id, cancellationToken);
            await StageEntityAsync(server, server.Id, cancellationToken);
        }

        foreach (var chat in changes.Chats)
        {
            await EnsureNotDeletedAsync(chat.Id, cancellationToken);
            await StageEntityAsync(chat, chat.Id, cancellationToken);
        }

        foreach (var binding in changes.Bindings)
        {
            await EnsureNotDeletedAsync(binding.Id, cancellationToken);
            await EnsureNotDeletedAsync(binding.TelegramChatId, cancellationToken);
            if (binding.ServerId.HasValue)
            {
                await EnsureNotDeletedAsync(binding.ServerId.Value, cancellationToken);
            }
            else if (binding.ContainerId.HasValue)
            {
                await EnsureNotDeletedAsync(binding.ContainerId.Value, cancellationToken);
            }

            await StageEntityAsync(binding, binding.Id, cancellationToken);
        }

        foreach (Tombstone tombstone in changes.Records.OfType<Tombstone>())
        {
            await CancelDeletedDeliveriesAsync(tombstone, cancellationToken);
            await StageDeletionAsync(tombstone, cancellationToken);
        }
    }

    private async Task EnsureNotDeletedAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await context.Set<Tombstone>().FindAsync([id], cancellationToken) is not null)
        {
            throw new InvalidOperationException("A tombstoned identity cannot be recreated.");
        }
    }

    private async Task StageAccountAsync(SharedAccount account, CancellationToken cancellationToken)
    {
        if (!account.DeletedAtUtc.HasValue)
        {
            await EnsureNotDeletedAsync(account.UserId, cancellationToken);
        }

        ApplicationRole? role = await context.Roles.FindAsync([account.RoleId], cancellationToken);
        if (role is null)
        {
            role = new ApplicationRole(account.RoleId, SharedAccount.ADMINISTRATOR)
            {
                NormalizedName = "ADMINISTRATOR",
                ConcurrencyStamp = account.RoleId.ToString("N")
            };
            context.Roles.Add(role);
        }
        else if (role.Name != SharedAccount.ADMINISTRATOR || role.NormalizedName != "ADMINISTRATOR")
        {
            throw new InvalidOperationException("The shared role must be Administrator.");
        }

        ApplicationUser? user = await context.Users.FindAsync([account.UserId], cancellationToken);
        if (user is null)
        {
            user = new ApplicationUser(account.UserId, account.Login);
            context.Users.Add(user);
        }

        user.ApplySharedAccount(account);
        List<Microsoft.AspNetCore.Identity.IdentityUserRole<Guid>> memberships = await context.UserRoles
            .Where(item => item.UserId == account.UserId).ToListAsync(cancellationToken);
        if (memberships.Any(item => item.RoleId != account.RoleId))
        {
            throw new InvalidOperationException("Only the shared Administrator role is supported.");
        }

        if (memberships.Count == 0)
        {
            context.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<Guid>
            {
                UserId = account.UserId,
                RoleId = account.RoleId
            });
        }
    }

    // Принятый пакет содержит разные защищённые сущности. EF identity resolution и SetValues
    // сохраняют единый tracker; публичного универсального репозитория этот метод не создаёт.
    private async Task StageEntityAsync(object entity, Guid id, CancellationToken cancellationToken)
    {
        object? tracked = await context.FindAsync(entity.GetType(), [id], cancellationToken);
        if (tracked is null)
        {
            context.Add(entity);
        }
        else if (!ReferenceEquals(tracked, entity))
        {
            if (tracked is MonitoredServer server && entity is MonitoredServer updatedServer)
            {
                if (server.Type != updatedServer.Type)
                {
                    throw new InvalidOperationException("The monitoring target type cannot be changed.");
                }

                server.UpdateSettings(updatedServer.Name, updatedServer.EndpointUrl,
                    updatedServer.Description, updatedServer.IsMonitoringEnabled);
                server.AssignServer(updatedServer.ServerId);
                server.AcceptConfigurationRevision(updatedServer.ConfigRevision, updatedServer.CheckGeneration);
                return;
            }

            if (tracked is NotificationBinding binding && entity is NotificationBinding updatedBinding)
            {
                if (binding.TelegramChatId != updatedBinding.TelegramChatId || binding.ServerId != updatedBinding.ServerId
                    || binding.ContainerId != updatedBinding.ContainerId)
                {
                    throw new InvalidOperationException("Binding identity cannot be moved.");
                }

                binding.AcceptBindingRevision(updatedBinding.BindingRevision, updatedBinding.BindingGeneration);
                return;
            }

            bool immutable = entity is OperationControl or Tombstone or ObserverResult
                or IncidentEvent or DeliverySummary or SummarySource;
            if (immutable)
            {
                var existingValues = context.Entry(tracked).CurrentValues;
                var suppliedValues = context.Entry(entity).CurrentValues;
                bool changed = existingValues.Properties.Any(property =>
                    !Equals(existingValues[property.Name], suppliedValues[property.Name]));
                if (changed)
                {
                    throw new InvalidOperationException("An immutable accepted record cannot be rewritten.");
                }

                return;
            }

            context.Entry(tracked).CurrentValues.SetValues(entity);
        }
    }

    private async Task StageDeletionAsync(Tombstone tombstone, CancellationToken cancellationToken)
    {
        if (tombstone.Kind is DeletedEntityKind.Server or DeletedEntityKind.Container)
        {
            List<NotificationBinding> bindings = await context.NotificationBindings
                .Where(binding => binding.ServerId == tombstone.Id || binding.ContainerId == tombstone.Id)
                .ToListAsync(cancellationToken);
            context.NotificationBindings.RemoveRange(bindings);
        }

        switch (tombstone.Kind)
        {
            case DeletedEntityKind.Server:
                await DeleteServerAsync(tombstone, cancellationToken);
                break;
            case DeletedEntityKind.Container:
                await RemoveAsync<Domain.Monitoring.MonitoredServer>(tombstone.Id, cancellationToken);
                break;
            case DeletedEntityKind.Chat:
                await RemoveAsync<Domain.Notifications.TelegramChat>(tombstone.Id, cancellationToken);
                break;
            case DeletedEntityKind.Binding:
                await RemoveAsync<Domain.Notifications.NotificationBinding>(tombstone.Id, cancellationToken);
                break;
            case DeletedEntityKind.Account:
                ApplicationUser? account = await context.Users.FindAsync([tombstone.Id], cancellationToken);
                if (account is not null && (account.IsEnabled || account.DeletedAtUtc is null))
                {
                    throw new InvalidOperationException("Account deletion requires its disabled shared account projection.");
                }

                break;
            case DeletedEntityKind.Node:
                await RemoveAsync<ClusterMember>(tombstone.Id, cancellationToken);
                break;
            case DeletedEntityKind.ObserverAssignment:
                await RemoveAsync<ObserverAssignment>(tombstone.Id, cancellationToken);
                break;
            case DeletedEntityKind.NodeChatRoute:
                await RemoveAsync<NodeChatRoute>(tombstone.Id, cancellationToken);
                break;
            default:
                throw new InvalidOperationException("Unsupported deletion kind.");
        }
    }

    private async Task DeleteServerAsync(Tombstone tombstone, CancellationToken cancellationToken)
    {
        var containers = await context.MonitoredServers.Where(item => item.ServerId == tombstone.Id)
            .ToListAsync(cancellationToken);
        foreach (var container in containers)
        {
            if (container.ConfigRevision < tombstone.LogIndex)
            {
                container.AcceptConfigurationRevision(tombstone.LogIndex, container.CheckGeneration);
            }

            container.AssignServer(null);
        }

        await RemoveAsync<Domain.Monitoring.MonitoredServer>(tombstone.Id, cancellationToken);
    }

    private async Task CancelDeletedDeliveriesAsync(Tombstone tombstone, CancellationToken cancellationToken)
    {
        IQueryable<DeliveryIntent> query = context.Set<DeliveryIntent>();
        if (tombstone.Kind == DeletedEntityKind.Chat)
        {
            query = query.Where(intent => intent.ChatId == tombstone.Id);
            var routes = await context.Set<NodeChatRoute>().Where(route => route.ChatId == tombstone.Id)
                .ToListAsync(cancellationToken);
            context.RemoveRange(routes);
        }
        else if (tombstone.Kind is DeletedEntityKind.Server or DeletedEntityKind.Container or DeletedEntityKind.Node)
        {
            query = query.Where(intent => context.Set<SharedIncident>()
                .Any(incident => incident.Id == intent.IncidentId && incident.TargetId == tombstone.Id));
        }
        else
        {
            return;
        }

        List<DeliveryIntent> intents = await query.ToListAsync(cancellationToken);
        foreach (DeliveryIntent intent in intents)
        {
            intent.CancelNotStarted(tombstone.DeletedAtUtc);
        }
    }

    private async Task RemoveAsync<TEntity>(Guid id, CancellationToken cancellationToken) where TEntity : class
    {
        TEntity? entity = await context.Set<TEntity>().FindAsync([id], cancellationToken);
        if (entity is not null)
        {
            context.Remove(entity);
        }
    }
}
