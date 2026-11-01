using System.Reflection;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Domain.Notifications;
using UptimeHarbor.Infrastructure.Identity;
using UptimeHarbor.Infrastructure.Persistence;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Явная граница общих таблиц/полей. Ключи, сессии, lockout и локальные очереди не экспортируются.</summary>
public static class SnapshotSchema
{
    private static readonly HashSet<string> USER_FIELDS =
    [
        "Id", "UserName", "NormalizedUserName", "PasswordHash", "PasswordHashFormatVersion",
        "SecurityStamp", "ConcurrencyStamp", "IsEnabled", "UserRevision", "DeletedAtUtc"
    ];

    public static IReadOnlyList<IEntityType> Entities(UptimeHarborDbContext context)
    {
        return context.Model.GetEntityTypes().Where(type =>
            typeof(ProjectionRecord).IsAssignableFrom(type.ClrType)
            || type.ClrType == typeof(MonitoredServer) || type.ClrType == typeof(MonitoredContainer)
            || type.ClrType == typeof(TelegramChat) || type.ClrType == typeof(NotificationBinding)
            || type.ClrType == typeof(ApplicationUser) || type.ClrType == typeof(ApplicationRole)
            || type.ClrType == typeof(IdentityUserRole<Guid>))
            .OrderBy(type => type.Name, StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<IProperty> Fields(IEntityType type)
    {
        IEnumerable<IProperty> fields = type.GetProperties();
        if (type.ClrType == typeof(ApplicationUser))
        {
            fields = fields.Where(property => USER_FIELDS.Contains(property.Name));
        }

        return fields.OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
    }

    public static async Task<IReadOnlyList<object>> ReadAsync(UptimeHarborDbContext context,
        Type entityType, CancellationToken cancellationToken)
    {
        MethodInfo method = typeof(SnapshotSchema).GetMethod(nameof(ReadTypedAsync), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(entityType);
        Task<IReadOnlyList<object>> pending = (Task<IReadOnlyList<object>>)method.Invoke(null, [context, cancellationToken])!;
        return await pending;
    }

    private static async Task<IReadOnlyList<object>> ReadTypedAsync<TEntity>(UptimeHarborDbContext context,
        CancellationToken cancellationToken) where TEntity : class
    {
        List<TEntity> entities = await context.Set<TEntity>().ToListAsync(cancellationToken);
        return entities.Cast<object>().ToArray();
    }

    public static SharedSnapshotRow Capture(UptimeHarborDbContext context, IEntityType type, object entity)
    {
        ImmutableDictionary<string, JsonElement>.Builder fields = ImmutableDictionary.CreateBuilder<string, JsonElement>();
        foreach (IProperty property in Fields(type))
        {
            object? value = context.Entry(entity).Property(property.Name).CurrentValue;
            fields.Add(property.Name, JsonSerializer.SerializeToElement(value, property.ClrType));
        }

        return new SharedSnapshotRow(type.Name, fields.ToImmutable());
    }

    public static void Validate(UptimeHarborDbContext context, IReadOnlyList<SharedSnapshotRow> rows)
    {
        Dictionary<string, IEntityType> types = Entities(context).ToDictionary(type => type.Name, StringComparer.Ordinal);
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (SharedSnapshotRow row in rows)
        {
            if (!types.TryGetValue(row.Entity, out IEntityType? type))
            {
                throw new InvalidDataException("UnsupportedSnapshotEntity");
            }

            IReadOnlyList<IProperty> fields = Fields(type);
            if (row.Fields.Count != fields.Count || fields.Any(field => !row.Fields.ContainsKey(field.Name)))
            {
                throw new InvalidDataException("SnapshotSchemaMismatch");
            }

            string key = row.Entity + ":" + string.Join("/", type.FindPrimaryKey()!.Properties
                .Select(property => row.Fields[property.Name].GetRawText()));
            if (!keys.Add(key))
            {
                throw new InvalidDataException("DuplicateSnapshotIdentity");
            }

            foreach (IProperty field in fields)
            {
                row.Fields[field.Name].Deserialize(field.ClrType);
            }
        }
    }
}
