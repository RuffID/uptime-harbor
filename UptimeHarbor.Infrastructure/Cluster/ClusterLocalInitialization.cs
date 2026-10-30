using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Persistence.UnitOfWork;

namespace UptimeHarbor.Infrastructure.Cluster;

public class ClusterLocalInitialization(IServiceScopeFactory scopes, IOptions<ClusterOptions> options) : IClusterLocalInitialization
{
    public async Task InitializeAsync(RecoveryAdmission admission, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        ClusterLocalInitializationUnitOfWork work = scope.ServiceProvider.GetRequiredService<ClusterLocalInitializationUnitOfWork>();
        await work.InitializeAsync(admission, options.Value.StorageGeneration.ToString("N"), cancellationToken);
    }
}
