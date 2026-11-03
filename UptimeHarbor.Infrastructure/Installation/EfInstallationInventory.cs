using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Persistence;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Проверяет каждую mapped таблицу, включая локальные очереди и Identity dependents.</summary>
public class EfInstallationInventory(UptimeHarborDbContext context) : IInstallationInventory
{
    public async Task<InstallationInventory> ReadAsync(CancellationToken token)
    {
        if ((await context.Database.GetPendingMigrationsAsync(token)).Any())
        {
            return new(false, new Dictionary<string, bool>());
        }

        Dictionary<string, bool> nonempty = new(StringComparer.Ordinal);
        foreach (IEntityType entity in context.Model.GetEntityTypes().Where(item => item.BaseType is null))
        {
            MethodInfo read = typeof(EfInstallationInventory).GetMethod(nameof(AnyAsync), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(entity.ClrType);
            nonempty.Add(entity.GetTableName()!, await (Task<bool>)read.Invoke(this, [token])!);
        }

        return new(true, nonempty);
    }

    private async Task<bool> AnyAsync<TEntity>(CancellationToken token) where TEntity : class
    {
        return await context.Set<TEntity>().AsNoTracking().AnyAsync(token);
    }
}
