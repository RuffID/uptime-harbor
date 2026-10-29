using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Identity;
using System.Collections.Immutable;
using System.Text.Json;

namespace UptimeHarbor.Infrastructure.Persistence.UnitOfWork;

/// <summary>Atomic install общих scalar-полей; локальные таблицы и локальные поля пользователя сохраняются.</summary>
public class SnapshotProjectionUnitOfWork(IUptimeHarborDbContextSession session, UptimeHarborDbContext context)
    : UptimeHarborDbUnitOfWork(session)
{
    public async Task<RevisionImage> ExportAsync(RevisionImage image, Guid nodeId, CancellationToken token)
    {
        ImmutableArray<SharedSnapshotRow>.Builder rows = ImmutableArray.CreateBuilder<SharedSnapshotRow>();
        await ExecuteInTransactionAsync(async cancellation =>
        {
            await VerifyCursorAsync(image, nodeId, cancellation);
            foreach (IEntityType type in SnapshotSchema.Entities(context))
            {
                IReadOnlyList<object> entities = await SnapshotSchema.ReadAsync(context, type.ClrType, cancellation);
                foreach (object entity in entities)
                {
                    rows.Add(SnapshotSchema.Capture(context, type, entity));
                }
            }
        }, token);

        return image with { ProjectionRows = rows.ToImmutable() };
    }

    public async Task VerifyCursorAsync(RevisionImage image, Guid nodeId, CancellationToken token)
    {
        ProjectionCursor? cursor = await context.Set<ProjectionCursor>().FindAsync([nodeId], token);
        if (cursor is null || cursor.ClusterId != image.ClusterId || cursor.ClusterGeneration != image.Generation
            || cursor.LogIndex != image.Index || cursor.Term != image.Term || cursor.Digest != image.Digest)
        {
            throw new InvalidDataException("ProjectionCheckpointMismatch");
        }
    }

    public async Task VerifyRecoveryAsync(RevisionImage image, Guid nodeId, CancellationToken token)
    {
        if (image.Index == 0)
        {
            if (await context.Set<ProjectionCursor>().AnyAsync(cursor => cursor.Id == nodeId, token))
            {
                throw new InvalidDataException("UnexpectedProjectionCheckpoint");
            }
        }
        else
        {
            await VerifyCursorAsync(image, nodeId, token);
        }

        List<ApplicationUser> users = await context.Users.AsNoTracking().ToListAsync(token);
        AcceptedValue[] accounts = image.Values.Values.Where(value => value.Kind == DeletedEntityKind.Account).ToArray();
        if (users.Count != accounts.Length)
        {
            throw new InvalidDataException("RecoveryAccountSetMismatch");
        }

        foreach (ApplicationUser user in users)
        {
            AcceptedValue? value = image.Values.GetValueOrDefault(user.Id);
            if (value is null || value.Kind != DeletedEntityKind.Account)
            {
                throw new InvalidDataException("RecoveryAccountHistoryMissing");
            }

            SharedAccount account = RevisionAccounts.Materialize(value);
            bool identityMismatch = user.UserRevision != account.UserRevision || user.IsEnabled != account.IsEnabled
                || user.DeletedAtUtc != account.DeletedAtUtc || user.UserName != account.Login
                || user.NormalizedUserName != account.NormalizedLogin;
            bool credentialMismatch = user.PasswordHash != account.Credential.PasswordHash
                || user.PasswordHashFormatVersion != account.Credential.FormatVersion
                || user.SecurityStamp != account.Credential.SecurityStamp || user.ConcurrencyStamp != account.Credential.ConcurrencyStamp;
            if (identityMismatch || credentialMismatch)
            {
                throw new InvalidDataException("RecoveryAccountMismatch");
            }
        }

        var memberships = await context.UserRoles.AsNoTracking().ToListAsync(token);
        List<ApplicationRole> roles = await context.Roles.AsNoTracking().ToListAsync(token);
        if (memberships.Count != accounts.Length || roles.Count != accounts.Select(account => account.Value.RoleId).Distinct().Count())
        {
            throw new InvalidDataException("RecoveryRoleSetMismatch");
        }

        foreach (AcceptedValue account in accounts)
        {
            bool member = memberships.Any(role => role.UserId == account.Id && role.RoleId == account.Value.RoleId);
            bool administrator = roles.Any(role => role.Id == account.Value.RoleId
                && role.Name == SharedAccount.ADMINISTRATOR && role.NormalizedName == "ADMINISTRATOR");
            if (!member || !administrator)
            {
                throw new InvalidDataException("RecoveryAdministratorRoleMissing");
            }
        }
    }

    public async Task InstallAsync(RevisionImage image, Guid nodeId, Guid snapshotId, string digest, CancellationToken token)
    {
        SnapshotImageValidation.Validate(context, image);
        await ExecuteInTransactionAsync(async cancellation =>
        {
            ProjectionCursor? cursor = await context.Set<ProjectionCursor>().FindAsync([nodeId], cancellation);
            if (cursor is not null && (cursor.ClusterId != image.ClusterId || cursor.ClusterGeneration != image.Generation
                || cursor.LogIndex > image.Index || cursor.Term > image.Term))
            {
                throw new InvalidDataException("StaleSnapshotProjection");
            }

            // Отсутствующий общий аккаунт нельзя оставить активным из старого backup.
            List<ApplicationUser> localUsers = await context.Users.ToListAsync(cancellation);
            foreach (ApplicationUser user in localUsers)
            {
                if (!image.Values.TryGetValue(user.Id, out AcceptedValue? account)
                    || account.Kind != DeletedEntityKind.Account || user.UserRevision > account.Revision)
                {
                    throw new InvalidDataException("SnapshotAccountHistoryMissing");
                }
            }

            foreach (IEntityType type in SnapshotSchema.Entities(context))
            {
                if (type.ClrType == typeof(ApplicationUser) || type.ClrType == typeof(ApplicationRole))
                {
                    continue;
                }

                context.RemoveRange(await SnapshotSchema.ReadAsync(context, type.ClrType, cancellation));
            }

            if (cursor is not null)
            {
                context.Remove(cursor);
            }

            // Оба SaveChanges принадлежат одной внешней транзакции; промежуточного commit нет.
            await SaveChangesAsync(cancellation);
            context.ChangeTracker.Clear();
            Dictionary<string, IEntityType> types = SnapshotSchema.Entities(context).ToDictionary(type => type.Name);
            foreach (SharedSnapshotRow row in image.ProjectionRows)
            {
                IEntityType type = types[row.Entity];
                object?[] key = type.FindPrimaryKey()!.Properties
                    .Select(property => row.Fields[property.Name].Deserialize(property.ClrType)).ToArray();
                object? entity = await context.FindAsync(type.ClrType, key, cancellation);
                bool create = entity is null;
                if (entity is null)
                {
                    entity = Activator.CreateInstance(type.ClrType, nonPublic: true)
                        ?? throw new InvalidDataException("SnapshotMaterializationFailed");
                }

                foreach (IProperty field in SnapshotSchema.Fields(type))
                {
                    context.Entry(entity).Property(field.Name).CurrentValue = row.Fields[field.Name].Deserialize(field.ClrType);
                }

                if (create)
                {
                    context.Add(entity);
                }
            }

            ProjectionCursor installed = new(nodeId, new ClusterIdentity(image.ClusterId, image.Generation));
            installed.Advance(0, new JournalPosition(new ClusterIdentity(image.ClusterId, image.Generation), image.Index, image.Term), image.Digest!);
            installed.RecordSnapshot(snapshotId);
            context.Add(installed);
            await RecordCheckpointAsync(nodeId, snapshotId, digest, image.Index, cancellation);
            await VerifyAccountsAsync(image, cancellation);
        }, token);
    }

    private async Task VerifyAccountsAsync(RevisionImage image, CancellationToken token)
    {
        foreach (AcceptedValue cell in image.Values.Values.Where(cell => cell.Kind == DeletedEntityKind.Account))
        {
            ApplicationUser? user = context.Users.Local.SingleOrDefault(user => user.Id == cell.Id);
            SharedAccount account = RevisionAccounts.Materialize(cell);
            bool mismatch = user is null || user.UserRevision != account.UserRevision || user.IsEnabled != account.IsEnabled
                || user.DeletedAtUtc != account.DeletedAtUtc || user.NormalizedUserName != account.NormalizedLogin
                || user.PasswordHash != account.Credential.PasswordHash || user.SecurityStamp != account.Credential.SecurityStamp;
            if (mismatch)
            {
                throw new InvalidDataException("SnapshotAccountMismatch");
            }

            bool membership = context.UserRoles.Local.Any(role => role.UserId == cell.Id && role.RoleId == account.RoleId);
            bool administrator = context.Roles.Local.Any(role => role.Id == account.RoleId
                && role.Name == SharedAccount.ADMINISTRATOR && role.NormalizedName == "ADMINISTRATOR");
            if (!membership || !administrator)
            {
                throw new InvalidDataException("SnapshotAdministratorRoleMissing");
            }
        }

        await Task.CompletedTask;
    }

    public async Task RecordCheckpointAsync(Guid nodeId, Guid snapshotId, string digest, long index,
        CancellationToken token)
    {
        LocalNodeState local = await context.Set<LocalNodeState>().FindAsync([nodeId], token)
            ?? throw new InvalidDataException("LocalRecoveryIdentityMissing");
        if (local.SnapshotId == snapshotId && local.SnapshotDigest == digest && local.SnapshotCutIndex == index)
        {
            return;
        }

        local.PublishSnapshotCheckpoint(snapshotId, digest, index);
    }
}
