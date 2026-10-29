using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

public static class RevisionValidation
{
    public static string? Validate(RevisionImage state, RevisionCommand command,
        AcceptedValue candidate, AcceptedValue? current)
    {
        try
        {
            if (candidate.Kind == DeletedEntityKind.Node)
            {
                return "NativeMembershipProtocolRequired";
            }

            if (current is not null && !candidate.Deleted
                && candidate.Kind is DeletedEntityKind.Server or DeletedEntityKind.Container)
            {
                bool checkChanged = current.Value.Endpoint != candidate.Value.Endpoint
                    || current.Value.Enabled != candidate.Value.Enabled;
                bool generationChanged = current.Value.Generation != candidate.Value.Generation;
                if (checkChanged != generationChanged)
                {
                    return "CheckGenerationMismatch";
                }
            }

            if (current is not null && candidate.Kind == DeletedEntityKind.Binding
                && (current.Value.ParentId != candidate.Value.ParentId || current.Value.ChatId != candidate.Value.ChatId
                    || current.Value.Enabled != candidate.Value.Enabled))
            {
                return "BindingIdentityCannotMove";
            }

            if (candidate.Kind == DeletedEntityKind.Account)
            {
                SharedAccount account = RevisionAccounts.Materialize(candidate);
                if (current is not null && current.Value.PasswordHash != candidate.Value.PasswordHash
                    && current.Value.SecurityStamp == candidate.Value.SecurityStamp)
                {
                    return "CredentialStampRequired";
                }

                Guid[] roleIds = state.Values.Values.Where(cell => cell.Kind == DeletedEntityKind.Account)
                    .Select(cell => cell.Value.RoleId).Distinct().ToArray();
                if (roleIds.Any(roleId => roleId != account.RoleId))
                {
                    return "OnlyAdministratorRole";
                }

                if (current is not null && (candidate.Deleted || !candidate.Value.Enabled))
                {
                    int active = state.Values.Values.Count(cell => cell.Kind == DeletedEntityKind.Account
                        && !cell.Deleted && cell.Value.Enabled);
                    RevisionAccounts.Materialize(current).EnsureCanDeactivate(command.ActorUserId, active);
                }
            }
            else if (!candidate.Deleted)
            {
                RevisionDomainMapper.Materialize(candidate, new JournalPosition(
                    new ClusterIdentity(state.ClusterId, state.Generation), state.Index, state.Term));
            }

            if (!candidate.Deleted)
            {
                string? dependencyError = CheckDependencies(state, candidate);
                if (dependencyError is not null)
                {
                    return dependencyError;
                }

                if (FindDuplicate(state, candidate) is not null)
                {
                    return "DuplicateKey";
                }
            }
        }
        catch (ArgumentException)
        {
            return "InvalidConfiguration";
        }
        catch (InvalidOperationException)
        {
            return "DomainInvariant";
        }

        return null;
    }

    public static AcceptedValue? FindDuplicate(RevisionImage state, AcceptedValue candidate)
    {
        foreach (AcceptedValue other in state.Values.Values.OrderBy(value => value.Id))
        {
            bool deletedKeyReleased = other.Deleted && other.Kind != DeletedEntityKind.Account;
            if (other.Id == candidate.Id || other.Kind != candidate.Kind || deletedKeyReleased || candidate.Deleted)
            {
                continue;
            }

            bool duplicate = false;
            if (candidate.Kind == DeletedEntityKind.Account)
            {
                duplicate = SharedAccount.NormalizeLogin(other.Value.Name!) == SharedAccount.NormalizeLogin(candidate.Value.Name!);
            }
            else if (candidate.Kind == DeletedEntityKind.Server)
            {
                duplicate = other.Value.Name!.Trim().ToUpperInvariant() == candidate.Value.Name!.Trim().ToUpperInvariant();
            }
            else if (candidate.Kind == DeletedEntityKind.Chat)
            {
                duplicate = other.Value.TelegramChatId == candidate.Value.TelegramChatId;
            }
            else if (candidate.Kind is DeletedEntityKind.Binding or DeletedEntityKind.NodeChatRoute or DeletedEntityKind.ObserverAssignment)
            {
                duplicate = other.Value.ParentId == candidate.Value.ParentId && other.Value.ChatId == candidate.Value.ChatId;
            }

            if (duplicate)
            {
                return other;
            }
        }

        return null;
    }

    private static string? CheckDependencies(RevisionImage state, AcceptedValue candidate)
    {
        ConfigurationValue value = candidate.Value;
        Guid?[] dependencies = [value.ParentId, value.ChatId];
        if (candidate.Kind != DeletedEntityKind.Account)
        {
            foreach (Guid? id in dependencies)
            {
                if (id.HasValue && state.Values.TryGetValue(id.Value, out AcceptedValue? dependency) && dependency.Deleted)
                {
                    return "DependencyDeleted";
                }
            }
        }

        if (candidate.Kind == DeletedEntityKind.Container && value.ParentId.HasValue
            && !Exists(state, value.ParentId, DeletedEntityKind.Server))
        {
            return "ParentDeletedOrMissing";
        }

        if (candidate.Kind == DeletedEntityKind.Binding)
        {
            DeletedEntityKind parentKind = DeletedEntityKind.Container;
            if (value.Enabled)
            {
                parentKind = DeletedEntityKind.Server;
            }

            if (!Exists(state, value.ParentId, parentKind) || !Exists(state, value.ChatId, DeletedEntityKind.Chat))
            {
                return "DependencyDeletedOrMissing";
            }
        }

        if (candidate.Kind == DeletedEntityKind.NodeChatRoute
            && (!Exists(state, value.ParentId, DeletedEntityKind.Node) || !Exists(state, value.ChatId, DeletedEntityKind.Chat)))
        {
            return "DependencyDeletedOrMissing";
        }

        if (candidate.Kind == DeletedEntityKind.ObserverAssignment)
        {
            bool target = Exists(state, value.ParentId, DeletedEntityKind.Server)
                || Exists(state, value.ParentId, DeletedEntityKind.Container);
            if (!target || !Exists(state, value.ChatId, DeletedEntityKind.Node))
            {
                return "DependencyDeletedOrMissing";
            }
        }

        return null;
    }

    private static bool Exists(RevisionImage state, Guid? id, DeletedEntityKind kind)
    {
        if (!id.HasValue || !state.Values.TryGetValue(id.Value, out AcceptedValue? value))
        {
            return false;
        }

        return value.Kind == kind && !value.Deleted;
    }
}
