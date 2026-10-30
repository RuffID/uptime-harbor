using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Infrastructure.Persistence;

namespace UptimeHarbor.Infrastructure.Cluster;

public class ClusterDatabaseProbe(IServiceScopeFactory scopes, NodeReadiness readiness, RevisionStateStore? accounts = null)
{
    public async Task ProbeAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        UptimeHarborDbContext context = scope.ServiceProvider.GetRequiredService<UptimeHarborDbContext>();
        bool healthy;
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            healthy = await context.Database.CanConnectAsync(timeout.Token);
            if (healthy)
            {
                // Connectivity не подтверждает совместимость схемы; migration здесь не выполняется.
                healthy = !(await context.Database.GetPendingMigrationsAsync(timeout.Token)).Any();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            healthy = false;
        }

        string reason = "DatabaseUnavailable";
        if (healthy)
        {
            reason = "Ready";
        }

        readiness.Report("Database", healthy, reason, TimeSpan.FromSeconds(10));
        if (!healthy)
        {
            accounts?.Invalidate();
        }
    }
}
