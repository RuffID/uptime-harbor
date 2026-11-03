using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UptimeHarbor.Application;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Web.Cluster;
using UptimeHarbor.Web.Configuration;
using UptimeHarbor.Web.Pages;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

public class SetupCompositionTests
{
    [Fact]
    public async Task CompleteSetupGraphHasOneSupervisorClosedAuthorityAndNoNativeStorage()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "setup-composition-" + Guid.NewGuid().ToString("N"));
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing", ContentRootPath = Path.GetTempPath(), Args = []
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Installation:InstallationIntent"] = "Setup",
            ["Installation:StateDirectory"] = Path.Combine(root, "state"),
            ["Installation:SecretDirectory"] = Path.Combine(root, "secret"),
            ["Installation:KeyDirectory"] = Path.Combine(root, "keys"),
            ["Installation:BrowserOrigin"] = "https://browser.example",
            ["Installation:PeerEndpoint"] = "https://peer.example:8443/internal/raft",
            ["Cluster:Mode"] = "Disabled", ["Cluster:StoragePath"] = Path.Combine(root, "storage"),
            ["Kestrel:Endpoints:Browser:Url"] = "https://localhost:443",
            ["Kestrel:Endpoints:Peer:Url"] = "https://localhost:8443",
            ["ConnectionStrings:SqlServer"] = "Server=unused.invalid;Database=unused;Integrated Security=true;Encrypt=true"
        });
        builder.ConfigureLocalInstallation().ConfigureClusterListeners();
        InstallationLease lease = (InstallationLease)builder.Services.Single(item => item.ServiceType == typeof(InstallationLease)).ImplementationInstance!;
        try
        {
            builder.Services.AddApplication().AddApplicationConfiguration(builder.Configuration)
                .AddInfrastructure(builder.Configuration).AddWebPresentation().AddClusterWeb();
            await using ServiceProvider provider = builder.Services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true, ValidateScopes = true
            });
            await using AsyncServiceScope scope = provider.CreateAsyncScope();

            // Act
            IHostedService[] workers = provider.GetServices<IHostedService>().Where(item => item is ManagedWorkerSupervisor).ToArray();
            SetupAuthorityCoordinator authority = scope.ServiceProvider.GetRequiredService<SetupAuthorityCoordinator>();
            InstallationLifecycle lifecycle = scope.ServiceProvider.GetRequiredService<InstallationLifecycle>();
            ClusterPeerRuntime runtime = provider.GetRequiredService<ClusterPeerRuntime>();

            // Assert
            Assert.Single(workers);
            Assert.NotNull(authority);
            Assert.NotNull(lifecycle);
            Assert.Null(runtime.Raft);
            Assert.False(Directory.Exists(Path.Combine(root, "storage", "wal")));
            Assert.False(Directory.Exists(Path.Combine(root, "storage", "membership")));
            Assert.IsType<UnavailableSetupAuthority>(provider.GetRequiredService<ISetupAuthority>());
            Assert.IsType<UnavailableManagedRuntimeAdmission>(provider.GetRequiredService<IManagedRuntimeAdmission>());
            Assert.False(provider.GetRequiredService<InstallationState>().CommonOpen);
            Assert.Null(provider.GetRequiredService<DynamicPeerTls>().Certificate);
        }
        finally
        {
            lease.Dispose();
            if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())
                || !Path.GetFileName(root).StartsWith("setup-composition-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("UnsafeTestCleanup");
            }

            Directory.Delete(root, true);
        }
    }
}
